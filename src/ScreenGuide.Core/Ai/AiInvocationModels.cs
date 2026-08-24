namespace ScreenGuide.Core.Ai;

public enum AiInvocationPurpose
{
    Conversation,
    SemanticIntent
}

public enum AiInvocationStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Interrupted
}

public sealed record AiTokenUsage(long InputTokens, long OutputTokens, long TotalTokens);

public sealed record AiInvocationRecord
{
    public required Guid Id { get; init; }

    public Guid? SessionTurnId { get; init; }

    public Guid? ConversationTurnId { get; init; }

    public required AiInvocationPurpose Purpose { get; init; }

    public required string ProviderId { get; init; }

    public required string ModelId { get; init; }

    public required string PromptId { get; init; }

    public required string PromptVersion { get; init; }

    public required string PromptContentHash { get; init; }

    public required string DataDestination { get; init; }

    public AiInvocationStatus Status { get; init; } = AiInvocationStatus.Running;

    public required DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public string? FinishReason { get; init; }

    public AiTokenUsage? Usage { get; init; }

    public string? ProviderRequestId { get; init; }

    public string? FailureCode { get; init; }
}
