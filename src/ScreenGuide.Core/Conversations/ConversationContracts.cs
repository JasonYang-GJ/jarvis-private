using ScreenGuide.Core.Memories;
using ScreenGuide.Core.Sessions;

namespace ScreenGuide.Core.Conversations;

public interface IConversationStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task CreateConversationAsync(
        ConversationRecord conversation,
        CancellationToken cancellationToken = default);

    Task<ConversationRecord?> GetConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationRecord>> GetConversationsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationMessageRecord>> GetMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationTurnRecord>> GetTurnsAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<ConversationTurnRegistration> StartTurnAsync(
        Guid conversationId,
        Guid turnId,
        string message,
        string idempotencyKey,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default);

    Task<ConversationTurnRegistration> StartTurnWithMemoryAsync(
        Guid conversationId,
        Guid turnId,
        string message,
        string idempotencyKey,
        MemoryOutboundAuditMetadata memoryOutbound,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default) =>
        StartTurnAsync(
            conversationId,
            turnId,
            message,
            idempotencyKey,
            startedAtUtc,
            cancellationToken);

    Task RecordProviderStartedAsync(
        Guid conversationId,
        Guid turnId,
        string externalThreadId,
        int processId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default);

    Task CompleteTurnAsync(
        Guid conversationId,
        Guid turnId,
        string reply,
        string? providerMessageId,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default);

    Task FailTurnAsync(
        Guid conversationId,
        Guid turnId,
        ConversationTurnStatus status,
        string failureCode,
        string failureMessage,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default);

    Task<ConversationRecoveryResult> RecoverInterruptedAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken = default);
}

public enum ConversationProviderOutcome
{
    Succeeded,
    Failed,
    Cancelled,
    Interrupted
}

public sealed record ConversationProviderRequest(
    Guid ConversationId,
    Guid ConversationTurnId,
    string Message,
    string? ExternalThreadId,
    Guid? SessionTurnId = null,
    SessionTurnFrozenRoute? FrozenRoute = null,
    MemoryOutboundEnvelope? MemoryOutbound = null)
{
    public Guid TurnId => ConversationTurnId;
}

public sealed record ConversationProviderResult(
    ConversationProviderOutcome Outcome,
    string? ExternalThreadId,
    string? Reply,
    string? ProviderMessageId,
    int? ProcessId,
    string? FailureCode = null,
    string? FailureMessage = null);

public interface IConversationProvider : IAsyncDisposable
{
    string ProviderId { get; }

    Task<ConversationProviderResult> SendAsync(
        ConversationProviderRequest request,
        Func<string, int, Task>? started = null,
        CancellationToken cancellationToken = default);

    Task CancelAsync(Guid conversationId, CancellationToken cancellationToken = default);
}
