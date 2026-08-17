using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScreenGuide.Agent.Abstractions;
using ScreenGuide.Core.Tasking;
using ScreenGuide.Evidence;
using ScreenGuide.Persistence.Runtime;
using AgentTaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed class AgentTaskExecutionService(
    ILocalTaskStore store,
    AgentConnectorRegistry connectorRegistry,
    TaskCancellationService cancellationService,
    TaskCancellationRegistry cancellationRegistry,
    TaskEvidenceService evidenceService,
    TimeProvider timeProvider) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Task> _attemptPumps = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _taskGates = new();
    private bool _disposed;

    public async Task<AgentRunReference> StartTaskAsync(
        Guid taskId,
        Guid? commandId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var gate = _taskGates.GetOrAdd(taskId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var task = await RequireTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (task.Status != AgentTaskStatus.Pending)
            {
                throw new InvalidOperationException("只有 Pending 任务可以首次启动 Agent。");
            }

            if (await store.GetAgentRunByTaskAsync(taskId, cancellationToken).ConfigureAwait(false)
                is not null)
            {
                throw new InvalidOperationException("任务已经存在 Agent Run，不能创建第二个 Thread。");
            }

            var project = await RequireAuthorizedProjectAsync(task.ProjectId, cancellationToken)
                .ConfigureAwait(false);
            var workingDirectory = ProjectPathPolicy.ResolveWithinRoot(
                project.RootPath,
                task.WorkingDirectoryRelativePath);
            await evidenceService.CaptureBaselineAsync(task, project, cancellationToken)
                .ConfigureAwait(false);
            var connector = connectorRegistry.GetRequired(task.Executor);
            var now = timeProvider.GetUtcNow();
            var run = new AgentRunRecord
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                ConnectorId = connector.ConnectorId,
                Transport = "exec-json-v1",
                Status = AgentRunStatus.Starting,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                LastEventSequence = 0
            };
            var attempt = NewAttempt(
                run,
                task,
                commandId,
                1,
                AgentAttemptOperation.Start,
                task.Instruction,
                now);
            await store.CreateAgentAttemptAsync(run, attempt, cancellationToken).ConfigureAwait(false);
            _ = cancellationRegistry.GetOrCreateToken(task.Id);

            var start = await connector.StartTaskAsync(
                    new AgentStartRequest(
                        task.Id,
                        project.RootPath,
                        workingDirectory,
                        task.Instruction,
                        attempt.Id),
                    cancellationToken)
                .ConfigureAwait(false);
            if (start.Status == AgentExecutionStatus.Running)
            {
                await RecordStartedAsync(task.Id, run.Id, attempt.Id, start, cancellationToken)
                    .ConfigureAwait(false);
            }

            StartPump(connector, start.Run, run.Id, attempt.Id, run.LastEventSequence);
            return start.Run;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AgentRunReference> ContinueTaskAsync(
        Guid taskId,
        string responseText,
        Guid? commandId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(responseText))
        {
            throw new ArgumentException("用户补充指令不能为空。", nameof(responseText));
        }

        var gate = _taskGates.GetOrAdd(taskId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var task = await RequireTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (task.Status != AgentTaskStatus.WaitingForUser)
            {
                throw new InvalidOperationException("只有 WaitingForUser 任务可以继续当前 Codex Thread。");
            }

            var run = await store.GetAgentRunByTaskAsync(taskId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("任务没有可续接的 Agent Run。");
            if (string.IsNullOrWhiteSpace(run.ExternalRunId))
            {
                throw new InvalidOperationException("Agent Run 没有持久化 ExternalRunId。");
            }

            var decision = await store.GetPendingDecisionRequestAsync(taskId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("任务没有待回答的 Decision Request。");
            var project = await RequireAuthorizedProjectAsync(task.ProjectId, cancellationToken)
                .ConfigureAwait(false);
            var workingDirectory = ProjectPathPolicy.ResolveWithinRoot(
                project.RootPath,
                task.WorkingDirectoryRelativePath);
            var attempts = await store.GetAgentAttemptsAsync(taskId, cancellationToken)
                .ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            var attempt = NewAttempt(
                run,
                task,
                commandId,
                attempts.Count + 1,
                AgentAttemptOperation.Resume,
                responseText,
                now);
            await store.CreateAgentAttemptAsync(run, attempt, cancellationToken).ConfigureAwait(false);
            _ = cancellationRegistry.GetOrCreateToken(task.Id);

            var connector = connectorRegistry.GetRequired(run.ConnectorId);
            var start = await connector.RespondToDecisionAsync(
                    new AgentDecisionResponse(
                        new AgentRunReference(task.Id, run.ExternalRunId),
                        decision.Id.ToString("D"),
                        responseText.Trim(),
                        attempt.Id,
                        project.RootPath,
                        workingDirectory,
                        run.LastEventSequence),
                    cancellationToken)
                .ConfigureAwait(false);
            if (start.Status == AgentExecutionStatus.Running)
            {
                await RecordStartedAsync(task.Id, run.Id, attempt.Id, start, cancellationToken)
                    .ConfigureAwait(false);
            }

            StartPump(connector, start.Run, run.Id, attempt.Id, run.LastEventSequence);
            return start.Run;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> CancelTaskAsync(
        Guid taskId,
        Guid sourceDeviceId,
        Guid? commandId = null,
        CancellationToken cancellationToken = default)
    {
        var run = await store.GetAgentRunByTaskAsync(taskId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("任务没有 Agent Run。");
        var attempts = await store.GetAgentAttemptsAsync(taskId, cancellationToken).ConfigureAwait(false);
        var attempt = attempts.LastOrDefault()
            ?? throw new InvalidOperationException("任务没有 Agent Attempt。");
        var accepted = await cancellationService.RequestAsync(
                taskId,
                sourceDeviceId,
                commandId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!accepted)
        {
            return false;
        }

        var requestedAt = timeProvider.GetUtcNow();
        await store.RecordAgentCancellationRequestedAsync(
                taskId,
                attempt.Id,
                requestedAt,
                cancellationToken)
            .ConfigureAwait(false);
        var connector = connectorRegistry.GetRequired(run.ConnectorId);
        await connector.CancelTaskAsync(
                new AgentRunReference(taskId, run.ExternalRunId, attempt.Id),
                cancellationToken)
            .ConfigureAwait(false);
        await WaitForAttemptAsync(attempt.Id, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task WaitForAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default) =>
        _attemptPumps.TryGetValue(attemptId, out var pump)
            ? pump.WaitAsync(cancellationToken)
            : Task.CompletedTask;

    public async Task WaitForTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var attempts = await store.GetAgentAttemptsAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (attempts.LastOrDefault() is { } attempt)
        {
            await WaitForAttemptAsync(attempt.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!connectorRegistry.IsInitialized)
        {
            return;
        }

        foreach (var connector in connectorRegistry.Connectors.OfType<IAsyncDisposable>())
        {
            await connector.DisposeAsync().ConfigureAwait(false);
        }

        var pumps = _attemptPumps.Values.ToArray();
        if (pumps.Length > 0)
        {
            await Task.WhenAll(pumps).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var gate in _taskGates.Values)
        {
            gate.Dispose();
        }
    }

    private void StartPump(
        IAgentConnector connector,
        AgentRunReference reference,
        Guid agentRunId,
        Guid attemptId,
        long afterSequence)
    {
        var pump = PumpEventsAsync(
            connector,
            reference with { AttemptId = attemptId },
            agentRunId,
            attemptId,
            afterSequence);
        if (!_attemptPumps.TryAdd(attemptId, pump))
        {
            throw new InvalidOperationException("Agent Attempt 事件泵重复启动。");
        }
    }

    private async Task PumpEventsAsync(
        IAgentConnector connector,
        AgentRunReference reference,
        Guid agentRunId,
        Guid attemptId,
        long afterSequence)
    {
        try
        {
            await foreach (var connectorEvent in connector.GetTaskEventsAsync(
                               reference,
                               afterSequence,
                               CancellationToken.None).ConfigureAwait(false))
            {
                var request = await MapEventAsync(
                        connector,
                        reference,
                        agentRunId,
                        attemptId,
                        connectorEvent)
                    .ConfigureAwait(false);
                _ = await store.ApplyAgentEventAsync(request, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            cancellationRegistry.Complete(reference.TaskId);
            _ = await evidenceService.FinalizeIfTerminalAsync(reference.TaskId, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task<AgentEventApplyRequest> MapEventAsync(
        IAgentConnector connector,
        AgentRunReference reference,
        Guid agentRunId,
        Guid attemptId,
        AgentConnectorEvent connectorEvent)
    {
        var status = connectorEvent.Status ?? InferStatus(connectorEvent.EventKind);
        var runStatus = MapRunStatus(status);
        var attemptStatus = MapAttemptStatus(status);
        var taskStatus = connectorEvent.EventKind switch
        {
            AgentConnectorEventKind.DecisionRequested => AgentTaskStatus.WaitingForUser,
            AgentConnectorEventKind.Completed => AgentTaskStatus.Succeeded,
            AgentConnectorEventKind.Failed => AgentTaskStatus.Failed,
            AgentConnectorEventKind.Cancelled => AgentTaskStatus.Cancelled,
            AgentConnectorEventKind.Interrupted => AgentTaskStatus.Interrupted,
            _ => null as AgentTaskStatus?
        };
        AgentFinalResult? final = null;
        DecisionRequestRecord? decision = null;
        if (connectorEvent.EventKind is AgentConnectorEventKind.DecisionRequested
            or AgentConnectorEventKind.Completed
            or AgentConnectorEventKind.Failed
            or AgentConnectorEventKind.Cancelled
            or AgentConnectorEventKind.Interrupted)
        {
            final = await connector.GetFinalResultAsync(reference, CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (connectorEvent.EventKind == AgentConnectorEventKind.DecisionRequested)
        {
            decision = ParseDecisionRequest(
                reference.TaskId,
                agentRunId,
                attemptId,
                connectorEvent);
        }

        var (failureCode, failureMessage, exitCode) = ParseDiagnostics(connectorEvent.DataJson);
        return new AgentEventApplyRequest
        {
            TaskId = reference.TaskId,
            AgentRunId = agentRunId,
            AttemptId = connectorEvent.AttemptId ?? attemptId,
            SequenceNumber = connectorEvent.SequenceNumber,
            ExternalEventId = connectorEvent.ExternalEventId
                ?? $"{attemptId:D}:{connectorEvent.SequenceNumber}",
            EventKind = connectorEvent.EventKind.ToString(),
            RunStatus = runStatus,
            AttemptStatus = attemptStatus,
            TaskStatus = taskStatus,
            OccurredAtUtc = connectorEvent.OccurredAtUtc,
            Message = connectorEvent.Message,
            DataJson = connectorEvent.DataJson,
            FinalSummary = final?.Summary,
            FinalResultJson = final?.DataJson,
            FailureCode = connectorEvent.EventKind == AgentConnectorEventKind.Failed
                ? failureCode ?? "agent_failed"
                : null,
            FailureMessage = connectorEvent.EventKind == AgentConnectorEventKind.Failed
                ? failureMessage ?? connectorEvent.Message
                : null,
            DecisionRequest = decision,
            ExitCode = final?.ExitCode ?? exitCode
        };
    }

    private static DecisionRequestRecord ParseDecisionRequest(
        Guid taskId,
        Guid agentRunId,
        Guid attemptId,
        AgentConnectorEvent connectorEvent)
    {
        if (connectorEvent.DataJson is null)
        {
            throw new InvalidDataException("DecisionRequested 事件缺少结构化数据。");
        }

        using var document = JsonDocument.Parse(connectorEvent.DataJson);
        var root = document.RootElement;
        var requestId = Guid.Parse(root.GetProperty("decisionRequestId").GetString()!);
        var question = root.GetProperty("question").GetString();
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new InvalidDataException("DecisionRequested 事件缺少 question。");
        }

        var options = root.GetProperty("decisionOptions").GetRawText();
        return new DecisionRequestRecord
        {
            Id = requestId,
            TaskId = taskId,
            AgentRunId = agentRunId,
            AttemptId = attemptId,
            Question = question,
            OptionsJson = options,
            Status = DecisionRequestStatus.Pending,
            CreatedAtUtc = connectorEvent.OccurredAtUtc
        };
    }

    private static (string? FailureCode, string? FailureMessage, int? ExitCode) ParseDiagnostics(
        string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return (null, null, null);
        }

        using var document = JsonDocument.Parse(dataJson);
        var root = document.RootElement;
        var code = root.TryGetProperty("failureCode", out var codeElement)
            && codeElement.ValueKind == JsonValueKind.String
                ? codeElement.GetString()
                : null;
        var message = root.TryGetProperty("failureMessage", out var messageElement)
            && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : null;
        int? exitCode = null;
        if (root.TryGetProperty("ExitCode", out var exitElement)
            && exitElement.ValueKind == JsonValueKind.Number
            && exitElement.TryGetInt32(out var parsed))
        {
            exitCode = parsed;
        }

        return (code, message, exitCode);
    }

    private async Task RecordStartedAsync(
        Guid taskId,
        Guid runId,
        Guid attemptId,
        AgentStartResult start,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(start.Run.ExternalRunId)
            || string.IsNullOrWhiteSpace(start.ConnectorVersion)
            || start.ProcessId is null)
        {
            throw new InvalidDataException("Connector 返回 Running，但缺少 Thread ID、版本或进程 ID。");
        }

        await store.RecordAgentStartedAsync(
                taskId,
                runId,
                attemptId,
                start.Run.ExternalRunId,
                start.ConnectorVersion,
                start.ProcessId.Value,
                start.StartedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AgentTask> RequireTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
        await store.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("任务不存在。");

    private async Task<ProjectRecord> RequireAuthorizedProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var project = await store.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("任务项目不存在。");
        if (project.AuthorizationState != ProjectAuthorizationState.Authorized)
        {
            throw new UnauthorizedAccessException("项目未授权，禁止启动 Codex。");
        }

        return project;
    }

    private static AgentAttemptRecord NewAttempt(
        AgentRunRecord run,
        AgentTask task,
        Guid? commandId,
        int attemptNumber,
        AgentAttemptOperation operation,
        string input,
        DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            AgentRunId = run.Id,
            TaskId = task.Id,
            CommandId = commandId,
            AttemptNumber = attemptNumber,
            Operation = operation,
            Status = AgentAttemptStatus.Starting,
            StartedAtUtc = now,
            LastEventSequence = run.LastEventSequence,
            InputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))
        };

    private static AgentExecutionStatus InferStatus(AgentConnectorEventKind kind) => kind switch
    {
        AgentConnectorEventKind.Started or AgentConnectorEventKind.Progress => AgentExecutionStatus.Running,
        AgentConnectorEventKind.DecisionRequested => AgentExecutionStatus.WaitingForUser,
        AgentConnectorEventKind.Completed => AgentExecutionStatus.Succeeded,
        AgentConnectorEventKind.Failed => AgentExecutionStatus.Failed,
        AgentConnectorEventKind.Cancelled => AgentExecutionStatus.Cancelled,
        AgentConnectorEventKind.Interrupted => AgentExecutionStatus.Interrupted,
        _ => AgentExecutionStatus.Unknown
    };

    private static AgentRunStatus MapRunStatus(AgentExecutionStatus status) => status switch
    {
        AgentExecutionStatus.Starting => AgentRunStatus.Starting,
        AgentExecutionStatus.Running or AgentExecutionStatus.Unknown => AgentRunStatus.Running,
        AgentExecutionStatus.WaitingForUser => AgentRunStatus.WaitingForUser,
        AgentExecutionStatus.Succeeded => AgentRunStatus.Succeeded,
        AgentExecutionStatus.Failed => AgentRunStatus.Failed,
        AgentExecutionStatus.Cancelled => AgentRunStatus.Cancelled,
        AgentExecutionStatus.Interrupted => AgentRunStatus.Interrupted,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static AgentAttemptStatus MapAttemptStatus(AgentExecutionStatus status) => status switch
    {
        AgentExecutionStatus.Starting => AgentAttemptStatus.Starting,
        AgentExecutionStatus.Running or AgentExecutionStatus.Unknown => AgentAttemptStatus.Running,
        AgentExecutionStatus.WaitingForUser or AgentExecutionStatus.Succeeded => AgentAttemptStatus.Completed,
        AgentExecutionStatus.Failed => AgentAttemptStatus.Failed,
        AgentExecutionStatus.Cancelled => AgentAttemptStatus.Cancelled,
        AgentExecutionStatus.Interrupted => AgentAttemptStatus.Interrupted,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
}
