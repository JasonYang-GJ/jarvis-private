namespace ScreenGuide.Skills.Abstractions;

public enum SkillAuthorizationOrigin
{
    ExplicitUser,
    Model,
    ScreenContent,
    Document,
    Website
}

public enum SkillExecutionStatus
{
    Starting,
    Running,
    WaitingForUser,
    Succeeded,
    Failed,
    Cancelled,
    Interrupted,
    Unknown
}

public enum SkillEventKind
{
    Started,
    Progress,
    DecisionRequested,
    Completed,
    Failed,
    Cancelled,
    Interrupted,
    Diagnostic
}

public sealed record SkillDescriptor(
    string Id,
    string Version,
    string DisplayName,
    bool IsBuiltIn,
    IReadOnlySet<string> Capabilities);

public sealed record SkillResourceScope(
    string ScopeType,
    Guid? ResourceId,
    string ScopeValue,
    string AccessMode);

public sealed record SkillInvocationRequest(
    Guid TaskId,
    Guid InvocationId,
    string SkillId,
    string Capability,
    string InputJson,
    IReadOnlyList<SkillResourceScope> ResourceScopes,
    SkillAuthorizationOrigin AuthorizationOrigin,
    Guid? ActionAuthorizationId,
    Guid? AttemptId = null);

public sealed record SkillRunReference(
    Guid TaskId,
    Guid InvocationId,
    string? ExternalRunId,
    Guid? AttemptId = null);

public sealed record SkillStartResult(
    SkillRunReference Run,
    SkillExecutionStatus Status,
    DateTimeOffset StartedAtUtc,
    string? AdapterVersion = null,
    int? ProcessId = null);

public sealed record SkillStatusSnapshot(
    SkillRunReference Run,
    SkillExecutionStatus Status,
    DateTimeOffset ObservedAtUtc,
    string? StatusMessage = null,
    string? DecisionRequestId = null);

public sealed record SkillEvent(
    long SequenceNumber,
    SkillEventKind EventKind,
    DateTimeOffset OccurredAtUtc,
    string Message,
    string? DataJson = null,
    SkillExecutionStatus? Status = null,
    string? ExternalEventId = null);

public sealed record SkillDecisionResponse(
    SkillRunReference Run,
    string RequestId,
    string ResponseText,
    Guid? AttemptId = null,
    long AfterSequence = 0);

public sealed record SkillFinalResult(
    SkillRunReference Run,
    bool Succeeded,
    DateTimeOffset CompletedAtUtc,
    string Summary,
    int? ExitCode = null,
    string? DataJson = null,
    bool ActionRequired = false,
    string? DecisionRequestId = null,
    string? Question = null);

public interface ISkillAdapter
{
    SkillDescriptor Descriptor { get; }

    Task<SkillStartResult> StartAsync(
        SkillInvocationRequest request,
        CancellationToken cancellationToken = default);

    Task<SkillStatusSnapshot> GetStatusAsync(
        SkillRunReference run,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<SkillEvent> GetEventsAsync(
        SkillRunReference run,
        long afterSequence,
        CancellationToken cancellationToken = default);

    Task CancelAsync(SkillRunReference run, CancellationToken cancellationToken = default);

    Task<SkillStartResult> RespondAsync(
        SkillDecisionResponse response,
        CancellationToken cancellationToken = default);

    Task<SkillFinalResult?> GetFinalResultAsync(
        SkillRunReference run,
        CancellationToken cancellationToken = default);
}
