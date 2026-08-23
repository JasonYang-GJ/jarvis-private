namespace ScreenGuide.Core.Sessions;

public interface ISessionStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task CreateSessionAsync(SessionRecord session, CancellationToken cancellationToken = default);
    Task<SessionRecord?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<SessionRecord?> GetSessionByConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<SessionRecord?> GetCurrentSessionAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionRecord>> GetSessionsAsync(CancellationToken cancellationToken = default);
    Task<SessionRecord> SetCurrentSessionAsync(Guid sessionId, DateTimeOffset changedAtUtc, CancellationToken cancellationToken = default);
    Task<SessionRecord> SetSelectedProjectAsync(Guid sessionId, Guid? projectId, DateTimeOffset changedAtUtc, CancellationToken cancellationToken = default);
    Task<SessionTurnRegistration> StartTurnAsync(Guid sessionId, string inputText, string inputModality, string idempotencyKey, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default);
    Task<SessionTurnRecord> UpdateTurnAsync(SessionTurnRecord turn, long expectedVersion, DateTimeOffset changedAtUtc, CancellationToken cancellationToken = default);
    Task<SessionTurnRecord?> GetTurnAsync(Guid turnId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionTurnRecord>> GetTurnsAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionTurnRecord>> GetActiveTurnsAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<SessionRecoveryResult> RecoverInterruptedAsync(DateTimeOffset recoveredAtUtc, CancellationToken cancellationToken = default);
}
