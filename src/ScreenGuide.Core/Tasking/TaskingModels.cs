namespace ScreenGuide.Core.Tasking;

public static class V01Contract
{
    public const int SchemaVersion = V02Contract.SchemaVersion;
}

public static class V02Contract
{
    public const int SchemaVersion = 11;

    public const int ProtocolVersion = 2;

    public const int MinimumProtocolVersion = 1;
}

public enum TaskStatus
{
    Pending,
    Running,
    WaitingForUser,
    CancellationRequested,
    Succeeded,
    Failed,
    Cancelled,
    Interrupted
}

public enum TaskEventType
{
    Created,
    StateChanged,
    PhaseChanged,
    AgentEvent,
    CancellationRequested,
    RecoveryDetected,
    Note
}

public enum TaskPhase
{
    Planning,
    Routing,
    AwaitingPermission,
    Executing,
    Verifying
}

public enum TaskEventSource
{
    System,
    User,
    Agent,
    Recovery
}

public enum ProjectAuthorizationState
{
    Authorized,
    Revoked
}

public enum ProjectAuthorizationScope
{
    ProjectDirectory
}

public enum ResourceScopeType
{
    Project,
    Directory,
    File,
    Window,
    Website
}

public enum ResourceAccessMode
{
    Observe,
    Read,
    Execute
}

public enum SkillInvocationStatus
{
    Pending,
    Running,
    WaitingForUser,
    Succeeded,
    Failed,
    Cancelled,
    Interrupted
}

public enum DeviceType
{
    WindowsHost,
    Mobile
}

public enum DeviceTrustState
{
    Local,
    Paired,
    Revoked
}

public enum CommandType
{
    CreateTask,
    CancelTask,
    ResumeTask,
    UserResponse,
    ExecuteDesktopAction
}

public enum CommandStatus
{
    Received,
    Processed,
    Rejected,
    Failed
}

public enum AuditOutcome
{
    Success,
    Rejected,
    Failed
}

public enum AgentRunStatus
{
    Starting,
    Running,
    WaitingForUser,
    Succeeded,
    Failed,
    Cancelled,
    Interrupted
}

public enum AgentAttemptStatus
{
    Starting,
    Running,
    Completed,
    Failed,
    Cancelled,
    Interrupted
}

public enum AgentAttemptOperation
{
    Start,
    Resume
}

public enum DecisionRequestStatus
{
    Pending,
    Answered,
    Cancelled
}

public sealed record AgentTask
{
    public required Guid Id { get; init; }

    public required Guid ProjectId { get; init; }

    public required Guid CreatedByDeviceId { get; init; }

    public required string Title { get; init; }

    public required string Instruction { get; init; }

    public string WorkingDirectoryRelativePath { get; init; } = ".";

    public string Executor { get; init; } = "codex";

    public TaskStatus Status { get; init; } = TaskStatus.Pending;

    public TaskPhase Phase { get; init; } = TaskPhase.Planning;

    public DateTimeOffset? CancellationRequestedAtUtc { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public DateTimeOffset? LastHeartbeatAtUtc { get; init; }

    public string? FailureCode { get; init; }

    public string? FailureMessage { get; init; }

    public long Version { get; init; }
}

public sealed record TaskEventRecord
{
    public required Guid Id { get; init; }

    public required Guid TaskId { get; init; }

    public required long SequenceNumber { get; init; }

    public required TaskEventType EventType { get; init; }

    public TaskStatus? FromStatus { get; init; }

    public TaskStatus? ToStatus { get; init; }

    public TaskPhase? FromPhase { get; init; }

    public TaskPhase? ToPhase { get; init; }

    public required TaskEventSource Source { get; init; }

    public Guid? SourceDeviceId { get; init; }

    public Guid? CommandId { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }

    public required string Message { get; init; }

    public string? DataJson { get; init; }
}

public sealed record ProjectRecord
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string RootPath { get; init; }

    public ProjectAuthorizationState AuthorizationState { get; init; } = ProjectAuthorizationState.Authorized;

    public required Guid AuthorizedByDeviceId { get; init; }

    public required DateTimeOffset AuthorizedAtUtc { get; init; }

    public DateTimeOffset? RevokedAtUtc { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record ProjectAuthorizationRecord
{
    public required Guid Id { get; init; }

    public required Guid ProjectId { get; init; }

    public ProjectAuthorizationScope Scope { get; init; } = ProjectAuthorizationScope.ProjectDirectory;

    public required string ScopeValue { get; init; }

    public ProjectAuthorizationState State { get; init; } = ProjectAuthorizationState.Authorized;

    public required Guid AuthorizedByDeviceId { get; init; }

    public required DateTimeOffset AuthorizedAtUtc { get; init; }

    public DateTimeOffset? RevokedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record ResourceScopeRecord
{
    public required Guid Id { get; init; }

    public required Guid TaskId { get; init; }

    public required ResourceScopeType ScopeType { get; init; }

    public Guid? ResourceId { get; init; }

    public required string ScopeValue { get; init; }

    public ResourceAccessMode AccessMode { get; init; } = ResourceAccessMode.Read;

    public required Guid GrantedByDeviceId { get; init; }

    public required DateTimeOffset GrantedAtUtc { get; init; }

    public DateTimeOffset? ExpiresAtUtc { get; init; }

    public DateTimeOffset? RevokedAtUtc { get; init; }
}

public sealed record SkillInvocationRecord
{
    public required Guid Id { get; init; }

    public required Guid TaskId { get; init; }

    public required int SequenceNumber { get; init; }

    public required string SkillId { get; init; }

    public required string SkillVersion { get; init; }

    public required string Capability { get; init; }

    public required string InputJson { get; init; }

    public SkillInvocationStatus Status { get; init; } = SkillInvocationStatus.Pending;

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public string? FailureCode { get; init; }

    public string? FailureMessage { get; init; }
}

public sealed record DeviceRecord
{
    public required Guid Id { get; init; }

    public required string DisplayName { get; init; }

    public required DeviceType DeviceType { get; init; }

    public required DeviceTrustState TrustState { get; init; }

    public string? PublicKeyThumbprint { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? LastSeenAtUtc { get; init; }

    public DateTimeOffset? RevokedAtUtc { get; init; }
}

public sealed record CommandRecord
{
    public required Guid Id { get; init; }

    public required Guid SourceDeviceId { get; init; }

    public Guid? ProjectId { get; init; }

    public Guid? TaskId { get; init; }

    public required string IdempotencyKey { get; init; }

    public required CommandType CommandType { get; init; }

    public required string PayloadJson { get; init; }

    public required DateTimeOffset ReceivedAtUtc { get; init; }

    public DateTimeOffset? ExpiresAtUtc { get; init; }

    public CommandStatus Status { get; init; } = CommandStatus.Received;

    public DateTimeOffset? ProcessedAtUtc { get; init; }

    public string? RejectionReason { get; init; }
}

public sealed record AuditLogEntry
{
    public required Guid Id { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }

    public Guid? ActorDeviceId { get; init; }

    public required string Action { get; init; }

    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    public required AuditOutcome Outcome { get; init; }

    public string? DetailsJson { get; init; }
}

public sealed record AgentRunRecord
{
    public required Guid Id { get; init; }

    public required Guid TaskId { get; init; }

    public required string ConnectorId { get; init; }

    public required string Transport { get; init; }

    public string? ExternalRunId { get; init; }

    public string? ConnectorVersion { get; init; }

    public AgentRunStatus Status { get; init; } = AgentRunStatus.Starting;

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public long LastEventSequence { get; init; }

    public string? LastEventType { get; init; }

    public DateTimeOffset? LastEventAtUtc { get; init; }

    public string? FinalSummary { get; init; }

    public string? FinalResultJson { get; init; }

    public string? FailureCode { get; init; }

    public string? FailureMessage { get; init; }
}

public sealed record AgentAttemptRecord
{
    public required Guid Id { get; init; }

    public required Guid AgentRunId { get; init; }

    public required Guid TaskId { get; init; }

    public Guid? CommandId { get; init; }

    public required int AttemptNumber { get; init; }

    public required AgentAttemptOperation Operation { get; init; }

    public AgentAttemptStatus Status { get; init; } = AgentAttemptStatus.Starting;

    public int? ProcessId { get; init; }

    public DateTimeOffset? ProcessStartedAtUtc { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset? TerminalEventAtUtc { get; init; }

    public DateTimeOffset? ProcessExitedAtUtc { get; init; }

    public string? TerminalEventType { get; init; }

    public int? ExitCode { get; init; }

    public DateTimeOffset? CancellationRequestedAtUtc { get; init; }

    public DateTimeOffset? CancellationConfirmedAtUtc { get; init; }

    public long LastEventSequence { get; init; }

    public DateTimeOffset? LastEventAtUtc { get; init; }

    public required string InputHash { get; init; }
}

public sealed record DecisionRequestRecord
{
    public required Guid Id { get; init; }

    public required Guid TaskId { get; init; }

    public required Guid AgentRunId { get; init; }

    public required Guid AttemptId { get; init; }

    public required string Question { get; init; }

    public required string OptionsJson { get; init; }

    public DecisionRequestStatus Status { get; init; } = DecisionRequestStatus.Pending;

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? RespondedAtUtc { get; init; }

    public Guid? ResponseCommandId { get; init; }
}

public sealed record AgentEventApplyRequest
{
    public required Guid TaskId { get; init; }

    public required Guid AgentRunId { get; init; }

    public required Guid AttemptId { get; init; }

    public required long SequenceNumber { get; init; }

    public required string ExternalEventId { get; init; }

    public required string EventKind { get; init; }

    public required AgentRunStatus RunStatus { get; init; }

    public required AgentAttemptStatus AttemptStatus { get; init; }

    public TaskStatus? TaskStatus { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }

    public required string Message { get; init; }

    public string? DataJson { get; init; }

    public string? FinalSummary { get; init; }

    public string? FinalResultJson { get; init; }

    public string? FailureCode { get; init; }

    public string? FailureMessage { get; init; }

    public DecisionRequestRecord? DecisionRequest { get; init; }

    public int? ExitCode { get; init; }
}

public sealed record AgentEventApplyResult(bool Applied, AgentTask Task);

public enum EvidenceVerificationStatus
{
    Verified,
    Unverified,
    VerificationFailed,
    Cancelled,
    Failed,
    Interrupted
}

public enum EvidenceFileChangeType
{
    Added,
    Modified,
    Deleted
}

public enum EvidenceTestStatus
{
    Passed,
    Failed,
    NotRun,
    Incomplete
}

public enum AgentClaimStatus
{
    Completed,
    ActionRequired,
    Failed,
    Unavailable
}

public sealed record EvidenceFileChange
{
    public required string RelativePath { get; init; }

    public required EvidenceFileChangeType ChangeType { get; init; }

    public bool HadPreExistingChanges { get; init; }

    public bool MixedWithPreExistingChanges { get; init; }

    public string? BeforeHash { get; init; }

    public string? AfterHash { get; init; }

    public int? AddedLines { get; init; }

    public int? DeletedLines { get; init; }

    public bool IsBinary { get; init; }
}

public sealed record GitStatusEvidence
{
    public required string RelativePath { get; init; }

    public required string StatusCode { get; init; }
}

public sealed record GitTaskEvidence
{
    public bool IsGitRepository { get; init; }

    public required DateTimeOffset BeforeCapturedAtUtc { get; init; }

    public required DateTimeOffset AfterCapturedAtUtc { get; init; }

    public required IReadOnlyList<string> PreExistingChangedFiles { get; init; }

    public required IReadOnlyList<GitStatusEvidence> BeforeStatus { get; init; }

    public required IReadOnlyList<GitStatusEvidence> AfterStatus { get; init; }

    public required IReadOnlyList<EvidenceFileChange> ChangedFiles { get; init; }

    public int AddedFileCount { get; init; }

    public int ModifiedFileCount { get; init; }

    public int DeletedFileCount { get; init; }

    public int? AddedLineCount { get; init; }

    public int? DeletedLineCount { get; init; }

    public int BinaryFileCount { get; init; }

    public bool DiffStatVerified { get; init; }

    public string? UnavailableReason { get; init; }
}

public sealed record TestCommandEvidence
{
    public required string Command { get; init; }

    public required string ExternalItemId { get; init; }

    public int? ExitCode { get; init; }

    public EvidenceTestStatus Status { get; init; }

    public int? TotalTests { get; init; }

    public int? PassedTests { get; init; }

    public int? FailedTests { get; init; }

    public int? SkippedTests { get; init; }
}

public sealed record TaskTestEvidence
{
    public EvidenceTestStatus Status { get; init; }

    public required IReadOnlyList<TestCommandEvidence> Commands { get; init; }

    public int? TotalTests { get; init; }

    public int? PassedTests { get; init; }

    public int? FailedTests { get; init; }

    public int? SkippedTests { get; init; }

    public bool HasRealExecutionEvidence { get; init; }
}

public sealed record AgentClaimEvidence
{
    public AgentClaimStatus Status { get; init; }

    public string? FinalExplanation { get; init; }

    public required IReadOnlyList<string> ClaimedChangedFiles { get; init; }

    public required IReadOnlyList<string> ClaimedTests { get; init; }
}

public sealed record ConnectorCompatibilityEvidence
{
    public required string ConnectorId { get; init; }

    public string? DetectedVersion { get; init; }

    public bool VersionVerified { get; init; }

    public required IReadOnlyList<string> VerifiedVersions { get; init; }

    public required string Decision { get; init; }
}

public sealed record TaskEvidence
{
    public required Guid Id { get; init; }

    public required Guid TaskId { get; init; }

    public required DateTimeOffset GeneratedAtUtc { get; init; }

    public required TaskStatus TaskStatus { get; init; }

    public required AgentClaimEvidence AgentClaim { get; init; }

    public required GitTaskEvidence Git { get; init; }

    public required TaskTestEvidence Tests { get; init; }

    public required ConnectorCompatibilityEvidence Connector { get; init; }

    public EvidenceVerificationStatus VerificationStatus { get; init; }

    public bool AgentClaimContradictedByEvidence { get; init; }

    public required IReadOnlyList<string> VerificationReasons { get; init; }

    public required string UserSummary { get; init; }
}
