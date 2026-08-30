using ScreenGuide.Core.Memories;

namespace ScreenGuide.Core.Sessions;

public enum SessionStatus
{
    Active,
    Archived
}

public enum SessionWorkKind
{
    Unknown,
    Conversation,
    DesktopAction,
    WindowObservation,
    CodingTask
}

public enum SessionTurnPhase
{
    Understanding,
    Responding,
    WaitingForProject,
    WaitingForFile,
    WaitingForWindow,
    WaitingForWindowConsent,
    WaitingForConfirmation,
    WaitingForMemoryOutboundConsent,
    Executing,
    ObservingWindow,
    ProgrammingTask,
    WaitingForUser,
    Completed,
    Failed,
    Cancelled,
    Interrupted
}

public enum SessionMissingContext
{
    None,
    Project,
    File,
    Window,
    WindowConsent,
    Confirmation,
    UserInput
}

public enum SessionTurnRouteStatus
{
    Ready,
    Unavailable
}

public sealed record SessionTurnFrozenRoute
{
    public required SessionTurnRouteStatus Status { get; init; }
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public string? DataDestination { get; init; }
    public bool? SendsDataOffDevice { get; init; }
    public required DateTimeOffset FrozenAtUtc { get; init; }
    public string? FailureCode { get; init; }
}

public sealed record SessionRecord
{
    public required Guid Id { get; init; }
    public required Guid ConversationId { get; init; }
    public required Guid CreatedByDeviceId { get; init; }
    public required string Title { get; init; }
    public SessionStatus Status { get; init; } = SessionStatus.Active;
    public bool IsCurrent { get; init; }
    public Guid? SelectedProjectId { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public required DateTimeOffset LastActiveAtUtc { get; init; }
    public long Version { get; init; }
}

public sealed record SessionTurnRecord
{
    public required Guid Id { get; init; }
    public required Guid SessionId { get; init; }
    public required int SequenceNumber { get; init; }
    public required string InputText { get; init; }
    public required string InputModality { get; init; }
    public required string IdempotencyKey { get; init; }
    public SessionTurnFrozenRoute? FrozenRoute { get; init; }
    public SessionWorkKind WorkKind { get; init; } = SessionWorkKind.Unknown;
    public SessionTurnPhase Phase { get; init; } = SessionTurnPhase.Understanding;
    public SessionMissingContext MissingContext { get; init; } = SessionMissingContext.None;
    public string? IntentKind { get; init; }
    public string? ExpectedIntentKind { get; init; }
    public string? ExpectedTarget { get; init; }
    public string? PlanTarget { get; init; }
    public Guid? PlanId { get; init; }
    public Guid? ConversationTurnId { get; init; }
    public Guid? TaskId { get; init; }
    public string? OperationId { get; init; }
    public Guid? ProjectId { get; init; }
    public string? FilePath { get; init; }
    public long? WindowHandle { get; init; }
    public string? WindowTitle { get; init; }
    public string? WindowProcessName { get; init; }
    public int? WindowProcessId { get; init; }
    public DateTimeOffset? WindowProcessStartTimeUtc { get; init; }
    public bool RequiresConfirmation { get; init; }
    public bool ConfirmationGranted { get; init; }
    public bool CancellationRequested { get; init; }
    public MemoryOutboundConsentState MemoryOutboundState { get; init; } = MemoryOutboundConsentState.None;
    public MemoryOutboundAuditMetadata? MemoryOutbound { get; init; }
    public string? ResultSummary { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public long Version { get; init; }
}

public sealed record SessionTurnRegistration(bool Accepted, SessionTurnRecord Turn);

public sealed record SessionTurnPage(
    IReadOnlyList<SessionTurnRecord> Items,
    int? NextBeforeSequenceNumber,
    bool HasMore);

public sealed record SessionRecoveryResult(IReadOnlyList<Guid> InterruptedTurnIds);

public static class SessionTurnPhases
{
    public static bool IsTerminal(SessionTurnPhase phase) => phase is
        SessionTurnPhase.Completed or
        SessionTurnPhase.Failed or
        SessionTurnPhase.Cancelled or
        SessionTurnPhase.Interrupted;

    public static bool IsForegroundWork(SessionTurnPhase phase) => phase is
        SessionTurnPhase.Understanding or
        SessionTurnPhase.Responding or
        SessionTurnPhase.WaitingForProject or
        SessionTurnPhase.WaitingForFile or
        SessionTurnPhase.WaitingForWindow or
        SessionTurnPhase.WaitingForWindowConsent or
        SessionTurnPhase.WaitingForConfirmation or
        SessionTurnPhase.WaitingForMemoryOutboundConsent or
        SessionTurnPhase.Executing or
        SessionTurnPhase.ObservingWindow;
}
