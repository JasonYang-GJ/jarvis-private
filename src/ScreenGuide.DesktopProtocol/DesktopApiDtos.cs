namespace ScreenGuide.DesktopProtocol;

public sealed record EmptyRequest;

public sealed record TaskIdRequest(Guid TaskId);

public sealed record ListTasksRequest(Guid? ProjectId = null, string? Status = null);

public sealed record CreateTaskRequestDto(
    Guid ProjectId,
    string Instruction,
    string? Title = null,
    string WorkingDirectoryRelativePath = ".",
    string? IdempotencyKey = null);

public sealed record ContinueTaskRequestDto(
    Guid TaskId,
    string Response,
    string? IdempotencyKey = null);

public sealed record CancelTaskRequestDto(Guid TaskId, string? IdempotencyKey = null);

public sealed record AddProjectRequestDto(string RootPath, string? Name = null);

public sealed record RevokeProjectRequestDto(Guid ProjectId);

public sealed record ClearHistoryRequestDto(bool Confirmed);

public sealed record DesktopApplicationDto(string Id, string DisplayName);

public sealed record ExecuteDesktopActionRequestDto(
    string ActionKind,
    string Target,
    bool Confirmed,
    string? IdempotencyKey = null,
    long? WindowHandle = null,
    string? WindowTitle = null,
    string? ApplicationId = null,
    string AuthorizationSource = "VisibleConfirmation");

public sealed record DesktopActionResultDto(
    Guid CommandId,
    Guid? InvocationId,
    bool Succeeded,
    string Message,
    bool WasDuplicate,
    int? ProcessId = null);

public sealed record PlanAssistantCommandRequestDto(
    string Text,
    string? SelectedFilePath = null,
    Guid? SelectedProjectId = null,
    bool ForegroundObservationConsent = false,
    string InputModality = "Text");

public sealed record ForegroundApplicationDto(
    long WindowHandle,
    string WindowTitle,
    string ProcessName,
    DateTimeOffset ObservedAtUtc);

public sealed record AssistantIntentPlanDto(
    Guid PlanId,
    string IntentKind,
    string Readiness,
    string UserSummary,
    bool RequiresConfirmation,
    string? ConfirmationText,
    string? MissingContext,
    DateTimeOffset? ExpiresAtUtc,
    ForegroundApplicationDto? ForegroundApplication = null,
    string? CanonicalTarget = null);

public sealed record ExecuteAssistantCommandRequestDto(
    Guid PlanId,
    bool Confirmed,
    string? IdempotencyKey = null,
    string AuthorizationSource = "VisibleConfirmation");

public sealed record CancelWindowObservationRequestDto(string OperationId);

public sealed record AssistantCommandResultDto(
    string IntentKind,
    string Status,
    string UserSummary,
    string VerificationStatus,
    Guid? TaskId = null,
    Guid? ConversationId = null,
    Guid? CommandId = null,
    bool WasDuplicate = false,
    AssistantActionEvidenceDto? Evidence = null);

public sealed record AssistantActionEvidenceDto(
    Guid EvidenceId,
    string VerificationStatus,
    string UserSummary,
    IReadOnlyList<string> VerifiedFacts,
    IReadOnlyList<string> UnverifiedFacts,
    DateTimeOffset GeneratedAtUtc);

public sealed record ConversationIdRequestDto(Guid ConversationId);

public sealed record CreateConversationRequestDto(string? Title = null);

public sealed record SendConversationMessageRequestDto(
    Guid ConversationId,
    string Message,
    string? IdempotencyKey = null);

public sealed record CancelConversationTurnRequestDto(Guid ConversationId);

public sealed record ConversationCommandResultDto(
    Guid ConversationId,
    Guid TurnId,
    bool WasDuplicate);

public sealed record ConversationSummaryDto(
    Guid Id,
    string Title,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastMessageAtUtc,
    string? FailureMessage);

public sealed record ConversationMessageDto(
    Guid Id,
    long SequenceNumber,
    string Role,
    string Content,
    DateTimeOffset CreatedAtUtc);

public sealed record ConversationTurnDto(
    Guid Id,
    int SequenceNumber,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureMessage);

public sealed record ConversationDetailsDto(
    ConversationSummaryDto Summary,
    IReadOnlyList<ConversationMessageDto> Messages,
    IReadOnlyList<ConversationTurnDto> Turns);

public sealed record SessionIdRequestDto(Guid SessionId);

public sealed record StartNewSessionRequestDto(string? Title = null);

public sealed record SessionInputRequestDto(
    string Text,
    string InputModality = "Text",
    string? IdempotencyKey = null,
    Guid? SessionId = null,
    string? ExpectedIntentKind = null,
    string? ExpectedTarget = null);

public sealed record ProvideSessionProjectRequestDto(
    Guid SessionId,
    Guid TurnId,
    Guid ProjectId);

public sealed record ProvideSessionFileRequestDto(
    Guid SessionId,
    Guid TurnId,
    string FilePath);

public sealed record SessionWindowConsentRequestDto(
    Guid SessionId,
    Guid TurnId,
    bool Granted);

public sealed record SessionTurnConfirmationRequestDto(
    Guid SessionId,
    Guid TurnId,
    bool Confirmed);

public sealed record CancelSessionTurnRequestDto(
    Guid SessionId,
    Guid TurnId);

public sealed record WaitForSessionUpdateRequestDto(
    long KnownChangeVersion,
    int WaitMilliseconds = 20_000);

public sealed record SessionTurnCommandResultDto(
    Guid SessionId,
    Guid TurnId,
    bool WasDuplicate);

public sealed record UnifiedSessionTurnDto(
    Guid Id,
    int SequenceNumber,
    string InputText,
    string InputModality,
    string WorkKind,
    string Phase,
    string MissingContext,
    string? IntentKind,
    Guid? ConversationTurnId,
    Guid? TaskId,
    Guid? ProjectId,
    string? FilePath,
    long? WindowHandle,
    string? WindowTitle,
    bool RequiresConfirmation,
    bool CancellationRequested,
    string? ResultSummary,
    string? FailureMessage,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ExpectedIntentKind = null,
    string? ExpectedTarget = null,
    string? PlanTarget = null);

public sealed record SessionSnapshotDto(
    long ChangeVersion,
    string CoordinatorInstanceId,
    DateTimeOffset CoordinatorStartedAtUtc,
    Guid SessionId,
    Guid ConversationId,
    string Title,
    string Status,
    Guid? SelectedProjectId,
    string? SelectedProjectName,
    UnifiedSessionTurnDto? ForegroundTurn,
    IReadOnlyList<UnifiedSessionTurnDto> ActiveTurns,
    IReadOnlyList<UnifiedSessionTurnDto> Turns,
    IReadOnlyList<ConversationMessageDto> Messages);

public sealed record CommandResultDto(Guid TaskId, bool WasDuplicate);

public sealed record CodexStatusDto(
    bool IsInstalled,
    bool IsCompatible,
    string? Version,
    string Message);

public sealed record SystemStatusDto(
    bool HostOnline,
    string HostVersion,
    DateTimeOffset StartedAtUtc,
    string DeviceId,
    string DatabaseStatus,
    string DataDirectory,
    string LogsDirectory,
    CodexStatusDto Codex,
    int ProtocolVersion = DesktopProtocolVersion.Current,
    int DatabaseSchemaVersion = 0);

public sealed record DashboardDto(
    SystemStatusDto System,
    int AuthorizedProjectCount,
    int ActiveTaskCount,
    int WaitingTaskCount,
    int CompletedTodayCount,
    int FailedTaskCount,
    IReadOnlyList<TaskSummaryDto> ActiveTasks,
    IReadOnlyList<TaskSummaryDto> WaitingTasks,
    IReadOnlyList<TaskSummaryDto> RecentTasks);

public sealed record ProjectDto(
    Guid Id,
    string Name,
    string RootPath,
    string AuthorizationState,
    bool IsGitRepository,
    bool HasGitChanges,
    string GitStatus,
    int RecentTaskCount,
    bool HasActiveTask,
    DateTimeOffset AuthorizedAtUtc,
    Guid? AuthorizationId = null);

public sealed record TaskSummaryDto(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    string Title,
    string Status,
    string? VerificationStatus,
    string? UserSummary,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureMessage,
    string Phase = "Planning");

public sealed record TaskEventDto(
    long SequenceNumber,
    string EventType,
    string? FromStatus,
    string? ToStatus,
    string Source,
    DateTimeOffset OccurredAtUtc,
    string Message,
    string? FromPhase = null,
    string? ToPhase = null);

public sealed record ResourceScopeDto(
    Guid Id,
    string ScopeType,
    Guid? ResourceId,
    string ScopeValue,
    string AccessMode,
    DateTimeOffset GrantedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc);

public sealed record SkillInvocationDto(
    Guid Id,
    int SequenceNumber,
    string SkillId,
    string SkillVersion,
    string Capability,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    string? FailureMessage);

public sealed record TaskAttemptDto(
    int AttemptNumber,
    string Operation,
    string Status,
    int? ProcessId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    int? ExitCode);

public sealed record DecisionRequestDto(Guid Id, string Question, DateTimeOffset CreatedAtUtc);

public sealed record EvidenceFileDto(
    string RelativePath,
    string ChangeType,
    bool HadPreExistingChanges,
    bool MixedWithPreExistingChanges,
    int? AddedLines,
    int? DeletedLines,
    bool IsBinary);

public sealed record TestCommandDto(
    string Command,
    int? ExitCode,
    string Status,
    int? TotalTests,
    int? PassedTests,
    int? FailedTests,
    int? SkippedTests);

public sealed record TaskEvidenceDto(
    string VerificationStatus,
    string UserSummary,
    bool AgentClaimContradictedByEvidence,
    string AgentClaimStatus,
    string? AgentFinalExplanation,
    bool IsGitRepository,
    int AddedFileCount,
    int ModifiedFileCount,
    int DeletedFileCount,
    int? AddedLineCount,
    int? DeletedLineCount,
    bool DiffStatVerified,
    IReadOnlyList<EvidenceFileDto> ChangedFiles,
    string TestStatus,
    bool HasRealTestEvidence,
    int? TotalTests,
    int? PassedTests,
    int? FailedTests,
    int? SkippedTests,
    IReadOnlyList<TestCommandDto> TestCommands,
    string? CodexVersion,
    bool CodexVersionVerified,
    string CompatibilityDecision,
    IReadOnlyList<string> VerificationReasons,
    DateTimeOffset GeneratedAtUtc);

public sealed record TaskDetailsDto(
    TaskSummaryDto Summary,
    ProjectDto Project,
    string Instruction,
    string WorkingDirectoryRelativePath,
    string? ThreadId,
    string? ConnectorVersion,
    int CurrentAttempt,
    IReadOnlyList<TaskAttemptDto> Attempts,
    DecisionRequestDto? PendingDecision,
    IReadOnlyList<TaskEventDto> Events,
    TaskEvidenceDto? Evidence,
    IReadOnlyList<ResourceScopeDto>? ResourceScopes = null,
    IReadOnlyList<SkillInvocationDto>? SkillInvocations = null);
