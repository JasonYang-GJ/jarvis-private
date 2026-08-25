namespace ScreenGuide.Core.Ai;

public interface IAiInvocationStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task StartAsync(
        AiInvocationRecord invocation,
        CancellationToken cancellationToken = default);

    Task<AiInvocationTransitionResult> CompleteAsync(
        Guid invocationId,
        string finishReason,
        AiTokenUsage? usage,
        string? providerRequestId,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default);

    Task<AiInvocationTransitionResult> FailAsync(
        Guid invocationId,
        AiInvocationStatus status,
        string failureCode,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default);

    Task<AiInvocationRecoveryResult> InterruptRunningAsync(
        DateTimeOffset interruptedAtUtc,
        string failureCode,
        CancellationToken cancellationToken = default);

    Task<AiInvocationRecord?> GetAsync(
        Guid invocationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AiInvocationRecord>> GetForConversationTurnAsync(
        Guid conversationTurnId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AiInvocationRecord>> GetForSessionTurnAsync(
        Guid sessionTurnId,
        CancellationToken cancellationToken = default);
}
