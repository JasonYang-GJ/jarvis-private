using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using ScreenGuide.Agent.Abstractions;

namespace ScreenGuide.Agent.Codex;

public sealed class CodexConnector : IAgentConnector, IAsyncDisposable
{
    private readonly CodexConnectorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly CodexCapabilityProbe _capabilityProbe;
    private readonly SemaphoreSlim _capabilityGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, RunState> _runs = new();
    private CodexCapability? _capability;
    private bool _disposed;

    public CodexConnector(CodexConnectorOptions options, TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _capabilityProbe = new CodexCapabilityProbe(new CodexExecutableLocator(options.ExecutablePath));
    }

    public string ConnectorId => "codex";

    public Task<AgentStartResult> StartTaskAsync(
        AgentStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Instruction))
        {
            throw new ArgumentException("Codex 任务指令不能为空。", nameof(request));
        }

        var paths = CodexProjectPathGuard.Validate(
            request.ProjectRootPath,
            request.WorkingDirectoryPath);
        var run = new RunState(request.TaskId, paths.ProjectRoot, paths.WorkingDirectory);
        if (!_runs.TryAdd(request.TaskId, run))
        {
            throw new InvalidOperationException("该任务已经存在 Codex Run。");
        }

        return StartAttemptAsync(
            run,
            request.AttemptId ?? Guid.NewGuid(),
            request.Instruction.Trim(),
            resume: false,
            cancellationToken);
    }

    public Task<AgentStatusSnapshot> GetTaskStatusAsync(
        AgentRunReference run,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = GetRun(run.TaskId);
        lock (state.Gate)
        {
            return Task.FromResult(
                new AgentStatusSnapshot(
                    new AgentRunReference(run.TaskId, state.ExternalRunId, run.AttemptId),
                    state.Status,
                    _timeProvider.GetUtcNow(),
                    state.StatusMessage,
                    state.DecisionRequestId));
        }
    }

    public async IAsyncEnumerable<AgentConnectorEvent> GetTaskEventsAsync(
        AgentRunReference run,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var state = GetRun(run.TaskId);
        var attempt = state.GetAttempt(run.AttemptId)
            ?? throw new InvalidOperationException("找不到 AgentRunReference 对应的 Codex Attempt。");
        await foreach (var connectorEvent in attempt.Events.Reader.ReadAllAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            if (connectorEvent.SequenceNumber > afterSequence)
            {
                yield return connectorEvent;
            }
        }
    }

    public async Task CancelTaskAsync(
        AgentRunReference run,
        CancellationToken cancellationToken = default)
    {
        var state = GetRun(run.TaskId);
        var attempt = state.GetAttempt(run.AttemptId) ?? state.ActiveAttempt;
        if (attempt is null)
        {
            throw new InvalidOperationException("当前 Codex Run 没有可取消的活动 Attempt。");
        }

        WindowsProcessJob? job;
        lock (attempt.Gate)
        {
            attempt.CancellationRequested = true;
            job = attempt.Job;
        }

        if (job is not null)
        {
            job.Terminate();
        }

        await attempt.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!attempt.ProcessTreeConfirmedStopped)
        {
            throw new InvalidOperationException("Codex 根进程已退出，但未能确认 Job Object 进程树停止。");
        }
    }

    public async Task<AgentStartResult> RespondToDecisionAsync(
        AgentDecisionResponse response,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(response);
        if (string.IsNullOrWhiteSpace(response.ResponseText))
        {
            throw new ArgumentException("用户补充指令不能为空。", nameof(response));
        }

        var state = _runs.GetOrAdd(
            response.Run.TaskId,
            _ => CreateResumedRunState(response));
        AttemptState? finishingAttempt;
        lock (state.Gate)
        {
            if (state.Status != AgentExecutionStatus.WaitingForUser
                && state.Status != AgentExecutionStatus.Interrupted)
            {
                throw new InvalidOperationException("Codex Run 当前没有等待用户决定或恢复指令。");
            }

            if (state.DecisionRequestId is not null
                && !string.Equals(
                    state.DecisionRequestId,
                    response.RequestId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Decision Request ID 不匹配。");
            }

            finishingAttempt = state.ActiveAttempt;
        }

        // turn.completed is authoritative for WaitingForUser, but the CLI process can
        // take a short time to flush and exit after that event. Do not start a second
        // turn until the previous process tree and event channel are fully closed.
        if (finishingAttempt is not null)
        {
            await finishingAttempt.Completion.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        lock (state.Gate)
        {
            if (state.Status != AgentExecutionStatus.WaitingForUser
                && state.Status != AgentExecutionStatus.Interrupted)
            {
                throw new InvalidOperationException("Codex Run 当前没有等待用户决定或恢复指令。");
            }

            if (state.ActiveAttempt is not null)
            {
                throw new InvalidOperationException("上一个 Codex Turn 尚未完成清理。");
            }
        }

        return await StartAttemptAsync(
                state,
                response.AttemptId ?? Guid.NewGuid(),
                response.ResponseText.Trim(),
                resume: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<AgentFinalResult?> GetFinalResultAsync(
        AgentRunReference run,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = GetRun(run.TaskId);
        lock (state.Gate)
        {
            return Task.FromResult(state.FinalResult);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var state in _runs.Values)
        {
            var attempt = state.ActiveAttempt;
            if (attempt?.Job is { } job)
            {
                try
                {
                    job.Terminate();
                }
                catch
                {
                    // Disposal still closes the job handle and applies KILL_ON_JOB_CLOSE.
                }
            }
        }

        var completions = _runs.Values
            .SelectMany(state => state.Attempts)
            .Select(attempt => attempt.Completion.Task)
            .ToArray();
        if (completions.Length > 0)
        {
            await Task.WhenAll(completions).ConfigureAwait(false);
        }

        _capabilityGate.Dispose();
    }

    private async Task<AgentStartResult> StartAttemptAsync(
        RunState run,
        Guid attemptId,
        string input,
        bool resume,
        CancellationToken cancellationToken)
    {
        var attempt = run.AddAttempt(attemptId);
        _ = Task.Run(
            () => RunAttemptAsync(run, attempt, input, resume),
            CancellationToken.None);
        return await attempt.Started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAttemptAsync(
        RunState run,
        AttemptState attempt,
        string input,
        bool resume)
    {
        Process? process = null;
        WindowsProcessJob? job = null;
        try
        {
            var capability = await EnsureCapabilityAsync(CancellationToken.None).ConfigureAwait(false);
            var schemaPath = await CodexOutputSchema.EnsureFileAsync(
                    _options.DataDirectory,
                    CancellationToken.None)
                .ConfigureAwait(false);
            process = CreateProcess(capability, run, schemaPath, resume);
            job = new WindowsProcessJob();
            if (!process.Start())
            {
                throw new InvalidOperationException("Codex 进程未能启动。");
            }

            try
            {
                job.Assign(process);
            }
            catch
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            lock (attempt.Gate)
            {
                attempt.Process = process;
                attempt.Job = job;
                attempt.ProcessId = process.Id;
                if (attempt.CancellationRequested)
                {
                    job.Terminate();
                }
            }

            var stderrTask = ReadStandardErrorAsync(process);
            var stdoutTask = ReadStandardOutputAsync(run, attempt, process, capability);
            await process.StandardInput.WriteAsync(input.AsMemory(), CancellationToken.None)
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            attempt.ExitCode = process.ExitCode;

            if (!attempt.AuthoritativeTerminalReceived)
            {
                if (attempt.CancellationRequested)
                {
                    EmitTerminal(
                        run,
                        attempt,
                        AgentConnectorEventKind.Cancelled,
                        AgentExecutionStatus.Cancelled,
                        "Codex 任务已取消，进程树已经停止。",
                        SyntheticEventId(attempt.Id, "cancelled"),
                        JsonSerializer.Serialize(new { process.ExitCode }));
                }
                else if (string.IsNullOrWhiteSpace(run.ExternalRunId))
                {
                    EmitTerminal(
                        run,
                        attempt,
                        AgentConnectorEventKind.Failed,
                        AgentExecutionStatus.Failed,
                        "Codex 在返回 Thread ID 前退出。",
                        SyntheticEventId(attempt.Id, "startup-exit"),
                        JsonSerializer.Serialize(new
                        {
                            process.ExitCode,
                            stderr = Sanitize(stderr)
                        }));
                }
                else
                {
                    EmitTerminal(
                        run,
                        attempt,
                        AgentConnectorEventKind.Interrupted,
                        AgentExecutionStatus.Interrupted,
                        "Codex 进程退出，但没有收到权威 Turn 终态。",
                        SyntheticEventId(attempt.Id, "missing-terminal"),
                        JsonSerializer.Serialize(new
                        {
                            process.ExitCode,
                            stderr = Sanitize(stderr)
                        }));
                }
            }
            else
            {
                Emit(
                    run,
                    attempt,
                    AgentConnectorEventKind.Diagnostic,
                    run.Status,
                    "Codex 进程已在权威 Turn 终态后退出。",
                    SyntheticEventId(attempt.Id, $"process-exit-{process.ExitCode}"),
                    JsonSerializer.Serialize(new { process.ExitCode }));
            }
        }
        catch (Exception exception)
        {
            if (!attempt.AuthoritativeTerminalReceived)
            {
                var status = attempt.CancellationRequested
                    ? AgentExecutionStatus.Cancelled
                    : string.IsNullOrWhiteSpace(run.ExternalRunId)
                        ? AgentExecutionStatus.Failed
                        : AgentExecutionStatus.Interrupted;
                var kind = status switch
                {
                    AgentExecutionStatus.Cancelled => AgentConnectorEventKind.Cancelled,
                    AgentExecutionStatus.Failed => AgentConnectorEventKind.Failed,
                    _ => AgentConnectorEventKind.Interrupted
                };
                EmitTerminal(
                    run,
                    attempt,
                    kind,
                    status,
                    status == AgentExecutionStatus.Cancelled
                        ? "Codex 任务已取消。"
                        : $"Codex 执行异常：{Sanitize(exception.Message)}",
                    SyntheticEventId(attempt.Id, $"exception-{exception.GetType().Name}"),
                    JsonSerializer.Serialize(new
                    {
                        exceptionType = exception.GetType().FullName,
                        message = Sanitize(exception.Message),
                        detectedVersion = (exception as CodexVersionCompatibilityException)?.DetectedVersion
                    }));
            }
        }
        finally
        {
            if (!attempt.Started.Task.IsCompleted)
            {
                attempt.Started.TrySetResult(
                    new AgentStartResult(
                        new AgentRunReference(run.TaskId, run.ExternalRunId, attempt.Id),
                        run.Status,
                        attempt.StartedAtUtc,
                        _capability?.Version,
                        attempt.ProcessId));
            }

            try
            {
                if (job is not null)
                {
                    if (job.HasActiveProcesses)
                    {
                        job.Terminate();
                    }

                    attempt.ProcessTreeConfirmedStopped = !job.HasActiveProcesses;
                }
                else
                {
                    attempt.ProcessTreeConfirmedStopped = process is null || process.HasExited;
                }
            }
            catch
            {
                attempt.ProcessTreeConfirmedStopped = false;
            }
            finally
            {
                job?.Dispose();
                process?.Dispose();
                lock (attempt.Gate)
                {
                    attempt.Job = null;
                    attempt.Process = null;
                }
            }

            attempt.Events.Writer.TryComplete();
            attempt.Completion.TrySetResult();
            run.CompleteAttempt(attempt);
        }
    }

    private Process CreateProcess(
        CodexCapability capability,
        RunState run,
        string schemaPath,
        bool resume)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = capability.ExecutablePath,
            WorkingDirectory = run.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("exec");
        if (resume)
        {
            startInfo.ArgumentList.Add("resume");
            startInfo.ArgumentList.Add("--json");
            startInfo.ArgumentList.Add("--output-schema");
            startInfo.ArgumentList.Add(schemaPath);
            if (!string.IsNullOrWhiteSpace(_options.Model))
            {
                startInfo.ArgumentList.Add("--model");
                startInfo.ArgumentList.Add(_options.Model);
            }

            startInfo.ArgumentList.Add(run.ExternalRunId!);
            startInfo.ArgumentList.Add("-");
        }
        else
        {
            startInfo.ArgumentList.Add("--json");
            startInfo.ArgumentList.Add("--sandbox");
            startInfo.ArgumentList.Add(_options.SandboxMode);
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add("approval_policy=\"never\"");
            startInfo.ArgumentList.Add("--cd");
            startInfo.ArgumentList.Add(run.WorkingDirectory);
            startInfo.ArgumentList.Add("--output-schema");
            startInfo.ArgumentList.Add(schemaPath);
            if (!string.IsNullOrWhiteSpace(_options.Model))
            {
                startInfo.ArgumentList.Add("--model");
                startInfo.ArgumentList.Add(_options.Model);
            }

            startInfo.ArgumentList.Add("-");
        }

        return new Process { StartInfo = startInfo };
    }

    private async Task ReadStandardOutputAsync(
        RunState run,
        AttemptState attempt,
        Process process,
        CodexCapability capability)
    {
        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)
               is { } line)
        {
            if (!CodexJsonLineParser.TryParse(line, out var protocolEvent, out var parseError))
            {
                if (parseError is not null)
                {
                    Emit(
                        run,
                        attempt,
                        AgentConnectorEventKind.Diagnostic,
                        run.Status,
                        parseError,
                        SyntheticEventId(attempt.Id, $"parse-{attempt.NextSyntheticId()}"),
                        null);
                }

                continue;
            }

            HandleProtocolEvent(run, attempt, protocolEvent!, capability);
        }
    }

    private static async Task<string> ReadStandardErrorAsync(Process process)
    {
        var value = await process.StandardError.ReadToEndAsync(CancellationToken.None)
            .ConfigureAwait(false);
        return Sanitize(value);
    }

    private void HandleProtocolEvent(
        RunState run,
        AttemptState attempt,
        CodexProtocolEvent protocolEvent,
        CodexCapability capability)
    {
        if (!attempt.SeenProtocolEvents.Add(protocolEvent.EventId))
        {
            return;
        }

        switch (protocolEvent.Type)
        {
            case "thread.started":
                if (string.IsNullOrWhiteSpace(protocolEvent.ThreadId))
                {
                    throw new InvalidDataException("thread.started 缺少 thread_id。");
                }

                run.SetExternalRunId(protocolEvent.ThreadId);
                Emit(
                    run,
                    attempt,
                    AgentConnectorEventKind.Started,
                    AgentExecutionStatus.Running,
                    "Codex Thread 已启动。",
                    protocolEvent.EventId,
                    JsonSerializer.Serialize(new { protocolEvent.Type }));
                attempt.Started.TrySetResult(
                    new AgentStartResult(
                        new AgentRunReference(run.TaskId, protocolEvent.ThreadId, attempt.Id),
                        AgentExecutionStatus.Running,
                        attempt.StartedAtUtc,
                        capability.Version,
                        attempt.ProcessId));
                break;

            case "turn.started":
                Emit(
                    run,
                    attempt,
                    AgentConnectorEventKind.Progress,
                    AgentExecutionStatus.Running,
                    "Codex Turn 正在运行。",
                    protocolEvent.EventId,
                    JsonSerializer.Serialize(new { protocolEvent.Type }));
                break;

            case "item.started":
            case "item.updated":
            case "item.completed":
                if (!string.IsNullOrWhiteSpace(protocolEvent.AgentMessageText))
                {
                    attempt.LastAgentMessage = protocolEvent.AgentMessageText;
                }

                Emit(
                    run,
                    attempt,
                    AgentConnectorEventKind.Progress,
                    AgentExecutionStatus.Running,
                    DescribeItem(protocolEvent),
                    protocolEvent.EventId,
                    JsonSerializer.Serialize(new
                    {
                        protocolEvent.Type,
                        protocolEvent.ItemId,
                        protocolEvent.ItemType,
                        protocolEvent.ItemStatus,
                        protocolEvent.ExitCode,
                        AttemptId = attempt.Id,
                        Command = SanitizeCommand(protocolEvent.Command),
                        TotalTests = protocolEvent.TestCounts?.Total,
                        PassedTests = protocolEvent.TestCounts?.Passed,
                        FailedTests = protocolEvent.TestCounts?.Failed,
                        SkippedTests = protocolEvent.TestCounts?.Skipped
                    }));
                break;

            case "error":
                Emit(
                    run,
                    attempt,
                    AgentConnectorEventKind.Diagnostic,
                    AgentExecutionStatus.Running,
                    $"Codex 报告错误：{Sanitize(protocolEvent.Message)}",
                    protocolEvent.EventId,
                    JsonSerializer.Serialize(new { protocolEvent.Type }));
                break;

            case "turn.failed":
                attempt.AuthoritativeTerminalReceived = true;
                EmitTerminal(
                    run,
                    attempt,
                    AgentConnectorEventKind.Failed,
                    AgentExecutionStatus.Failed,
                    $"Codex Turn 明确失败：{Sanitize(protocolEvent.Message)}",
                    protocolEvent.EventId,
                    JsonSerializer.Serialize(new
                    {
                        protocolEvent.Type,
                        failureCode = "codex_turn_failed",
                        failureMessage = Sanitize(protocolEvent.Message)
                    }));
                break;

            case "turn.completed":
                attempt.AuthoritativeTerminalReceived = true;
                HandleCompletedTurn(run, attempt, protocolEvent);
                break;

            default:
                Emit(
                    run,
                    attempt,
                    AgentConnectorEventKind.Diagnostic,
                    run.Status,
                    $"忽略未知 Codex 事件：{protocolEvent.Type}",
                    protocolEvent.EventId,
                    JsonSerializer.Serialize(new { protocolEvent.Type }));
                break;
        }
    }

    private void HandleCompletedTurn(
        RunState run,
        AttemptState attempt,
        CodexProtocolEvent protocolEvent)
    {
        if (!CodexTurnResultParser.TryParse(
                attempt.LastAgentMessage,
                out var result,
                out var parseError))
        {
            EmitTerminal(
                run,
                attempt,
                AgentConnectorEventKind.Failed,
                AgentExecutionStatus.Failed,
                parseError!,
                protocolEvent.EventId,
                JsonSerializer.Serialize(new
                {
                    protocolEvent.Type,
                    failureCode = "invalid_structured_result",
                    failureMessage = parseError
                }));
            return;
        }

        if (result!.Outcome == "action_required")
        {
            var requestId = Guid.NewGuid().ToString("D");
            var final = new AgentFinalResult(
                new AgentRunReference(run.TaskId, run.ExternalRunId, attempt.Id),
                false,
                _timeProvider.GetUtcNow(),
                result.Summary,
                DataJson: result.Json,
                ActionRequired: true,
                DecisionRequestId: requestId,
                Question: result.Question);
            run.SetFinal(
                AgentExecutionStatus.WaitingForUser,
                final,
                result.Summary,
                requestId);
            Emit(
                run,
                attempt,
                AgentConnectorEventKind.DecisionRequested,
                AgentExecutionStatus.WaitingForUser,
                result.Summary,
                protocolEvent.EventId,
                JsonSerializer.Serialize(new
                {
                    protocolEvent.Type,
                    decisionRequestId = requestId,
                    question = result.Question,
                    decisionOptions = result.DecisionOptions,
                    finalResult = JsonSerializer.Deserialize<JsonElement>(result.Json)
                }));
            return;
        }

        var completed = new AgentFinalResult(
            new AgentRunReference(run.TaskId, run.ExternalRunId, attempt.Id),
            true,
            _timeProvider.GetUtcNow(),
            result.Summary,
            DataJson: result.Json);
        run.SetFinal(AgentExecutionStatus.Succeeded, completed, result.Summary, null);
        Emit(
            run,
            attempt,
            AgentConnectorEventKind.Completed,
            AgentExecutionStatus.Succeeded,
            result.Summary,
            protocolEvent.EventId,
            JsonSerializer.Serialize(new
            {
                protocolEvent.Type,
                finalResult = JsonSerializer.Deserialize<JsonElement>(result.Json)
            }));
    }

    private void EmitTerminal(
        RunState run,
        AttemptState attempt,
        AgentConnectorEventKind kind,
        AgentExecutionStatus status,
        string message,
        string eventId,
        string? dataJson)
    {
        attempt.AuthoritativeTerminalReceived |= kind is AgentConnectorEventKind.Completed
            or AgentConnectorEventKind.Failed;
        AgentFinalResult? final = null;
        if (kind is AgentConnectorEventKind.Failed
            or AgentConnectorEventKind.Cancelled
            or AgentConnectorEventKind.Interrupted)
        {
            final = new AgentFinalResult(
                new AgentRunReference(run.TaskId, run.ExternalRunId, attempt.Id),
                false,
                _timeProvider.GetUtcNow(),
                message,
                attempt.ExitCode,
                dataJson);
            run.SetFinal(status, final, message, null);
        }

        Emit(run, attempt, kind, status, message, eventId, dataJson);
    }

    private void Emit(
        RunState run,
        AttemptState attempt,
        AgentConnectorEventKind kind,
        AgentExecutionStatus status,
        string message,
        string eventId,
        string? dataJson)
    {
        run.SetStatus(status, message);
        var sequence = run.NextSequence();
        attempt.Events.Writer.TryWrite(
            new AgentConnectorEvent(
                sequence,
                kind,
                _timeProvider.GetUtcNow(),
                message,
                dataJson,
                status,
                attempt.Id,
                $"{attempt.Id:D}:{eventId}"));
    }

    private async Task<CodexCapability> EnsureCapabilityAsync(CancellationToken cancellationToken)
    {
        if (_capability is not null)
        {
            return _capability;
        }

        await _capabilityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _capability ??= await _capabilityProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            return _capability;
        }
        finally
        {
            _capabilityGate.Release();
        }
    }

    private RunState GetRun(Guid taskId) =>
        _runs.TryGetValue(taskId, out var state)
            ? state
            : throw new KeyNotFoundException("找不到 Codex Run。");

    private static RunState CreateResumedRunState(AgentDecisionResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.Run.ExternalRunId)
            || string.IsNullOrWhiteSpace(response.ProjectRootPath)
            || string.IsNullOrWhiteSpace(response.WorkingDirectoryPath))
        {
            throw new InvalidOperationException("Host 重启后续接 Codex 需要 Thread ID 和授权项目路径。");
        }

        var paths = CodexProjectPathGuard.Validate(
            response.ProjectRootPath,
            response.WorkingDirectoryPath);
        return new RunState(
            response.Run.TaskId,
            paths.ProjectRoot,
            paths.WorkingDirectory,
            response.Run.ExternalRunId,
            AgentExecutionStatus.Interrupted,
            response.AfterSequence);
    }

    private static string DescribeItem(CodexProtocolEvent protocolEvent) =>
        protocolEvent.ItemType switch
        {
            "command_execution" => $"Codex 命令执行状态：{protocolEvent.ItemStatus ?? protocolEvent.Type}",
            "file_change" => $"Codex 文件修改状态：{protocolEvent.ItemStatus ?? protocolEvent.Type}",
            "agent_message" => "Codex 已生成一条结构化回复。",
            null => $"Codex 事件：{protocolEvent.Type}",
            _ => $"Codex 项目事件：{protocolEvent.ItemType}"
        };

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "无错误详情";
        }

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    private static string? SanitizeCommand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.ReplaceLineEndings(" ").Trim();
        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"(?i)(--?(?:api[-_]?key|token|password|secret)(?:=|\s+))[^\s]+",
            "$1<redacted>");
        return normalized.Length <= 1000 ? normalized : normalized[..1000];
    }

    private static string SyntheticEventId(Guid attemptId, string suffix) =>
        $"synthetic-{attemptId:D}-{suffix}";

    private sealed class RunState
    {
        private readonly Dictionary<Guid, AttemptState> _attempts = [];
        private long _sequence;

        public RunState(
            Guid taskId,
            string projectRoot,
            string workingDirectory,
            string? externalRunId = null,
            AgentExecutionStatus status = AgentExecutionStatus.Starting,
            long initialSequence = 0)
        {
            TaskId = taskId;
            ProjectRoot = projectRoot;
            WorkingDirectory = workingDirectory;
            ExternalRunId = externalRunId;
            Status = status;
            _sequence = initialSequence;
        }

        public object Gate { get; } = new();

        public Guid TaskId { get; }

        public string ProjectRoot { get; }

        public string WorkingDirectory { get; }

        public string? ExternalRunId { get; private set; }

        public AgentExecutionStatus Status { get; private set; }

        public string? StatusMessage { get; private set; }

        public string? DecisionRequestId { get; private set; }

        public AgentFinalResult? FinalResult { get; private set; }

        public AttemptState? ActiveAttempt { get; private set; }

        public IReadOnlyList<AttemptState> Attempts
        {
            get
            {
                lock (Gate)
                {
                    return _attempts.Values.ToArray();
                }
            }
        }

        public AttemptState AddAttempt(Guid attemptId)
        {
            lock (Gate)
            {
                if (ActiveAttempt is not null)
                {
                    throw new InvalidOperationException("同一 Codex Run 不能并发执行多个 Turn。");
                }

                var attempt = new AttemptState(attemptId);
                if (!_attempts.TryAdd(attemptId, attempt))
                {
                    throw new InvalidOperationException("Codex Attempt ID 重复。");
                }

                ActiveAttempt = attempt;
                Status = AgentExecutionStatus.Starting;
                StatusMessage = "Codex 进程正在启动。";
                DecisionRequestId = null;
                FinalResult = null;
                return attempt;
            }
        }

        public AttemptState? GetAttempt(Guid? attemptId)
        {
            lock (Gate)
            {
                if (attemptId is { } id)
                {
                    return _attempts.GetValueOrDefault(id);
                }

                return ActiveAttempt ?? _attempts.Values.LastOrDefault();
            }
        }

        public void CompleteAttempt(AttemptState attempt)
        {
            lock (Gate)
            {
                if (ReferenceEquals(ActiveAttempt, attempt))
                {
                    ActiveAttempt = null;
                }
            }
        }

        public void SetExternalRunId(string externalRunId)
        {
            lock (Gate)
            {
                if (ExternalRunId is not null
                    && !string.Equals(ExternalRunId, externalRunId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Codex resume 返回了不同的 Thread ID。");
                }

                ExternalRunId = externalRunId;
            }
        }

        public void SetStatus(AgentExecutionStatus status, string message)
        {
            lock (Gate)
            {
                Status = status;
                StatusMessage = message;
            }
        }

        public void SetFinal(
            AgentExecutionStatus status,
            AgentFinalResult finalResult,
            string message,
            string? decisionRequestId)
        {
            lock (Gate)
            {
                Status = status;
                FinalResult = finalResult;
                StatusMessage = message;
                DecisionRequestId = decisionRequestId;
            }
        }

        public long NextSequence() => Interlocked.Increment(ref _sequence);
    }

    private sealed class AttemptState(Guid id)
    {
        private int _syntheticId;

        public object Gate { get; } = new();

        public Guid Id { get; } = id;

        public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;

        public Channel<AgentConnectorEvent> Events { get; } =
            Channel.CreateUnbounded<AgentConnectorEvent>(
                new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

        public TaskCompletionSource<AgentStartResult> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HashSet<string> SeenProtocolEvents { get; } = new(StringComparer.Ordinal);

        public Process? Process { get; set; }

        public WindowsProcessJob? Job { get; set; }

        public int? ProcessId { get; set; }

        public int? ExitCode { get; set; }

        public string? LastAgentMessage { get; set; }

        public bool AuthoritativeTerminalReceived { get; set; }

        public bool CancellationRequested { get; set; }

        public bool ProcessTreeConfirmedStopped { get; set; }

        public int NextSyntheticId() => Interlocked.Increment(ref _syntheticId);
    }
}
