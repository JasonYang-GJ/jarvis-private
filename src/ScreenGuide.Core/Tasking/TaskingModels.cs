namespace ScreenGuide.Core.Tasking;

public static class V01Contract
{
    public const int SchemaVersion = 1;
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
    CancellationRequested,
    RecoveryDetected,
    Note
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
    UserResponse
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
