using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ScreenGuide.Agent.Abstractions;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Agent.Codex;

public sealed record CodexSkillInput(
    string ProjectRootPath,
    string WorkingDirectoryPath,
    string Instruction);

/// <summary>
/// 将 Codex CLI 生命周期封装为通用 Skill Contract；上层不需要理解 JSONL 或 CLI 参数。
/// </summary>
public sealed class CodexSkillAdapter(IAgentConnector connector) : ISkillAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, InvocationContext> _contexts = new();

    public SkillDescriptor Descriptor { get; } = new(
        "codex.project-task",
        "0.2.0",
        "Codex 项目任务",
        IsBuiltIn: true,
        new HashSet<string>(StringComparer.Ordinal) { "coding.execute" });

    public async Task<SkillStartResult> StartAsync(
        SkillInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var input = DeserializeInput(request.InputJson);
        ValidateScope(request, input);
        var context = new InvocationContext(input);
        if (!_contexts.TryAdd(request.InvocationId, context))
        {
            throw new InvalidOperationException("这个技能调用已经启动，不能重复执行。");
        }

        try
        {
            var result = await connector.StartTaskAsync(
                new AgentStartRequest(
                    request.TaskId,
                    input.ProjectRootPath,
                    input.WorkingDirectoryPath,
                    input.Instruction,
                    request.AttemptId),
                cancellationToken).ConfigureAwait(false);
            context.Run = result.Run;
            return MapStart(request.InvocationId, result);
        }
        catch
        {
            _contexts.TryRemove(request.InvocationId, out _);
            throw;
        }
    }

    public async Task<SkillStatusSnapshot> GetStatusAsync(
        SkillRunReference run,
        CancellationToken cancellationToken = default)
    {
        var status = await connector.GetTaskStatusAsync(ToAgentRun(run), cancellationToken)
            .ConfigureAwait(false);
        return new SkillStatusSnapshot(
            run with { ExternalRunId = status.Run.ExternalRunId, AttemptId = status.Run.AttemptId },
            MapStatus(status.Status),
            status.ObservedAtUtc,
            status.StatusMessage,
            status.DecisionRequestId);
    }

    public async IAsyncEnumerable<SkillEvent> GetEventsAsync(
        SkillRunReference run,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in connector.GetTaskEventsAsync(
                           ToAgentRun(run),
                           afterSequence,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return new SkillEvent(
                item.SequenceNumber,
                MapEventKind(item.EventKind),
                item.OccurredAtUtc,
                item.Message,
                item.DataJson,
                item.Status is null ? null : MapStatus(item.Status.Value),
                item.ExternalEventId);
        }
    }

    public Task CancelAsync(SkillRunReference run, CancellationToken cancellationToken = default) =>
        connector.CancelTaskAsync(ToAgentRun(run), cancellationToken);

    public async Task<SkillStartResult> RespondAsync(
        SkillDecisionResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!_contexts.TryGetValue(response.Run.InvocationId, out var context))
        {
            throw new InvalidOperationException("找不到这个技能调用的安全执行上下文。");
        }

        var result = await connector.RespondToDecisionAsync(
            new AgentDecisionResponse(
                ToAgentRun(response.Run),
                response.RequestId,
                response.ResponseText,
                response.AttemptId,
                context.Input.ProjectRootPath,
                context.Input.WorkingDirectoryPath,
                response.AfterSequence),
            cancellationToken).ConfigureAwait(false);
        context.Run = result.Run;
        return MapStart(response.Run.InvocationId, result);
    }

    public async Task<SkillFinalResult?> GetFinalResultAsync(
        SkillRunReference run,
        CancellationToken cancellationToken = default)
    {
        var result = await connector.GetFinalResultAsync(ToAgentRun(run), cancellationToken)
            .ConfigureAwait(false);
        return result is null
            ? null
            : new SkillFinalResult(
                run with
                {
                    ExternalRunId = result.Run.ExternalRunId,
                    AttemptId = result.Run.AttemptId
                },
                result.Succeeded,
                result.CompletedAtUtc,
                result.Summary,
                result.ExitCode,
                result.DataJson,
                result.ActionRequired,
                result.DecisionRequestId,
                result.Question);
    }

    private void ValidateRequest(SkillInvocationRequest request)
    {
        if (!string.Equals(request.SkillId, Descriptor.Id, StringComparison.Ordinal)
            || !string.Equals(request.Capability, "coding.execute", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Codex Skill 不允许执行这个能力。");
        }

        if (request.AuthorizationOrigin != SkillAuthorizationOrigin.ExplicitUser
            || request.ActionAuthorizationId is null)
        {
            throw new UnauthorizedAccessException("Codex Skill 需要用户本次明确授权。");
        }
    }

    private static void ValidateScope(SkillInvocationRequest request, CodexSkillInput input)
    {
        var scope = request.ResourceScopes.Count == 1 ? request.ResourceScopes[0] : null;
        if (scope is null
            || !string.Equals(scope.ScopeType, "Project", StringComparison.Ordinal)
            || !string.Equals(scope.AccessMode, "Execute", StringComparison.Ordinal)
            || scope.ResourceId is null
            || !string.Equals(
                Path.GetFullPath(scope.ScopeValue),
                Path.GetFullPath(input.ProjectRootPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Codex Skill 的项目执行范围与用户授权不一致。");
        }
    }

    private static CodexSkillInput DeserializeInput(string inputJson)
    {
        try
        {
            var input = JsonSerializer.Deserialize<CodexSkillInput>(inputJson, JsonOptions)
                ?? throw new InvalidDataException("Codex Skill 输入为空。");
            if (string.IsNullOrWhiteSpace(input.ProjectRootPath)
                || string.IsNullOrWhiteSpace(input.WorkingDirectoryPath)
                || string.IsNullOrWhiteSpace(input.Instruction))
            {
                throw new InvalidDataException("Codex Skill 输入缺少项目、工作目录或任务指令。");
            }

            return input with { Instruction = input.Instruction.Trim() };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Codex Skill 输入不是有效的 JSON。", exception);
        }
    }

    private static SkillStartResult MapStart(Guid invocationId, AgentStartResult result) =>
        new(
            new SkillRunReference(
                result.Run.TaskId,
                invocationId,
                result.Run.ExternalRunId,
                result.Run.AttemptId),
            MapStatus(result.Status),
            result.StartedAtUtc,
            result.ConnectorVersion,
            result.ProcessId);

    private static AgentRunReference ToAgentRun(SkillRunReference run) =>
        new(run.TaskId, run.ExternalRunId, run.AttemptId);

    private static SkillExecutionStatus MapStatus(AgentExecutionStatus status) => status switch
    {
        AgentExecutionStatus.Starting => SkillExecutionStatus.Starting,
        AgentExecutionStatus.Running => SkillExecutionStatus.Running,
        AgentExecutionStatus.WaitingForUser => SkillExecutionStatus.WaitingForUser,
        AgentExecutionStatus.Succeeded => SkillExecutionStatus.Succeeded,
        AgentExecutionStatus.Failed => SkillExecutionStatus.Failed,
        AgentExecutionStatus.Cancelled => SkillExecutionStatus.Cancelled,
        AgentExecutionStatus.Interrupted => SkillExecutionStatus.Interrupted,
        _ => SkillExecutionStatus.Unknown
    };

    private static SkillEventKind MapEventKind(AgentConnectorEventKind eventKind) => eventKind switch
    {
        AgentConnectorEventKind.Started => SkillEventKind.Started,
        AgentConnectorEventKind.Progress => SkillEventKind.Progress,
        AgentConnectorEventKind.DecisionRequested => SkillEventKind.DecisionRequested,
        AgentConnectorEventKind.Completed => SkillEventKind.Completed,
        AgentConnectorEventKind.Failed => SkillEventKind.Failed,
        AgentConnectorEventKind.Cancelled => SkillEventKind.Cancelled,
        AgentConnectorEventKind.Interrupted => SkillEventKind.Interrupted,
        _ => SkillEventKind.Diagnostic
    };

    private sealed class InvocationContext(CodexSkillInput input)
    {
        public CodexSkillInput Input { get; } = input;

        public AgentRunReference? Run { get; set; }
    }
}
