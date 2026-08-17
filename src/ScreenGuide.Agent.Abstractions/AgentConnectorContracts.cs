namespace ScreenGuide.Agent.Abstractions;

public enum AgentExecutionStatus
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

public enum AgentConnectorEventKind
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

public sealed record AgentStartRequest(
    Guid TaskId,
    string ProjectRootPath,
    string WorkingDirectoryPath,
    string Instruction,
    Guid? AttemptId = null);

public sealed record AgentRunReference(
    Guid TaskId,
    string? ExternalRunId,
    Guid? AttemptId = null);

public sealed record AgentStartResult(
    AgentRunReference Run,
    AgentExecutionStatus Status,
    DateTimeOffset StartedAtUtc,
    string? ConnectorVersion = null,
    int? ProcessId = null);

public sealed record AgentStatusSnapshot(
    AgentRunReference Run,
    AgentExecutionStatus Status,
    DateTimeOffset ObservedAtUtc,
    string? StatusMessage = null,
    string? DecisionRequestId = null);

public sealed record AgentConnectorEvent(
    long SequenceNumber,
    AgentConnectorEventKind EventKind,
    DateTimeOffset OccurredAtUtc,
    string Message,
    string? DataJson = null,
    AgentExecutionStatus? Status = null,
    Guid? AttemptId = null,
    string? ExternalEventId = null);

public sealed record AgentDecisionResponse(
    AgentRunReference Run,
    string RequestId,
    string ResponseText,
    Guid? AttemptId = null,
    string? ProjectRootPath = null,
    string? WorkingDirectoryPath = null,
    long AfterSequence = 0);

public sealed record AgentFinalResult(
    AgentRunReference Run,
    bool Succeeded,
    DateTimeOffset CompletedAtUtc,
    string Summary,
    int? ExitCode = null,
    string? DataJson = null,
    bool ActionRequired = false,
    string? DecisionRequestId = null,
    string? Question = null);
