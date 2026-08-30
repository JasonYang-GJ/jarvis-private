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
    Task<SessionTurnRegistration> StartTurnAsync(Guid sessionId, string inputText, string inputModality, string idempotencyKey, SessionTurnFrozenRoute frozenRoute, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default);
    Task<SessionTurnRecord> UpdateTurnAsync(SessionTurnRecord turn, long expectedVersion, DateTimeOffset changedAtUtc, CancellationToken cancellationToken = default);
    Task<SessionTurnRecord?> GetTurnAsync(Guid turnId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionTurnRecord>> GetTurnsAsync(Guid sessionId, CancellationToken cancellationToken = default);
    async Task<SessionTurnPage> GetTurnsPageAsync(
        Guid sessionId,
        int? beforeSequenceNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        var values = (await GetTurnsAsync(sessionId, cancellationToken).ConfigureAwait(false))
            .Where(item => beforeSequenceNumber is null || item.SequenceNumber < beforeSequenceNumber)
            .OrderByDescending(item => item.SequenceNumber)
            .Take(pageSize + 1)
            .ToArray();
        var hasMore = values.Length > pageSize;
        var items = values.Take(pageSize).OrderBy(item => item.SequenceNumber).ToArray();
        return new SessionTurnPage(
            items,
            hasMore && items.Length > 0 ? items[0].SequenceNumber : null,
            hasMore);
    }
    Task<IReadOnlyList<SessionTurnRecord>> GetActiveTurnsAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<SessionRecoveryResult> RecoverInterruptedAsync(DateTimeOffset recoveredAtUtc, CancellationToken cancellationToken = default);
}
