using ScreenGuide.Core.Memories;

namespace ScreenGuide.Core.Conversations;

public enum ConversationStatus
{
    Ready,
    Responding,
    Failed,
    Interrupted
}

public enum ConversationMessageRole
{
    User,
    Assistant,
    System
}

public enum ConversationTurnStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Interrupted
}

public sealed record ConversationRecord
{
    public required Guid Id { get; init; }

    public required Guid CreatedByDeviceId { get; init; }

    public required string Title { get; init; }

    public string ProviderId { get; init; } = "codex-conversation";

    public string? ExternalThreadId { get; init; }

    public ConversationStatus Status { get; init; } = ConversationStatus.Ready;

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public DateTimeOffset? LastMessageAtUtc { get; init; }

    public string? FailureCode { get; init; }

    public string? FailureMessage { get; init; }

    public long Version { get; init; }
}

public sealed record ConversationMessageRecord
{
    public required Guid Id { get; init; }

    public required Guid ConversationId { get; init; }

    public required long SequenceNumber { get; init; }

    public required ConversationMessageRole Role { get; init; }

    public required string Content { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public string? ProviderMessageId { get; init; }
}

public sealed record ConversationTurnRecord
{
    public required Guid Id { get; init; }

    public required Guid ConversationId { get; init; }

    public required int SequenceNumber { get; init; }

    public required Guid UserMessageId { get; init; }

    public Guid? AssistantMessageId { get; init; }

    public required string IdempotencyKey { get; init; }

    public ConversationTurnStatus Status { get; init; } = ConversationTurnStatus.Running;

    public int? ProcessId { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public string? FailureCode { get; init; }

    public string? FailureMessage { get; init; }

    public bool MemoryDerived { get; init; }

    public MemoryOutboundAuditMetadata? MemoryOutbound { get; init; }
}

public sealed record ConversationTurnRegistration(
    bool Accepted,
    ConversationTurnRecord Turn,
    ConversationMessageRecord UserMessage);

public sealed record ConversationRecoveryResult(IReadOnlyList<Guid> InterruptedConversationIds);
