using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Memories;
using ScreenGuide.Core.Sessions;

namespace ScreenGuide.DesktopHost.Runtime;

/// <summary>
/// Read-only bounded Session projection owner. SessionCoordinator remains the only
/// writer and records committed changes here only after persistence succeeds.
/// </summary>
public sealed class SessionProjectionService(
    ISessionStore sessionStore,
    IConversationStore conversationStore,
    LocalTaskEntryService tasks)
{
    private readonly object _changeGate = new();
    private readonly SessionChangeJournal _changeJournal = new();
    private TaskCompletionSource<long> _nextChange = NewChangeSource();
    private long _changeVersion = 1;

    public string CoordinatorInstanceId { get; } = Guid.NewGuid().ToString("N");

    public Guid CoordinatorInstanceGuid => Guid.Parse(CoordinatorInstanceId);

    public DateTimeOffset CoordinatorStartedAtUtc { get; } = DateTimeOffset.UtcNow;

    public long ChangeVersion
    {
        get
        {
            lock (_changeGate)
            {
                return _changeVersion;
            }
        }
    }

    public void RecordChange(Guid? sessionId = null, SessionTurnRecord? turnUpsert = null)
    {
        TaskCompletionSource<long> completed;
        long version;
        lock (_changeGate)
        {
            version = ++_changeVersion;
            completed = _nextChange;
            _nextChange = NewChangeSource();
        }

        if (turnUpsert is null)
        {
            _changeJournal.AppendReset(version, sessionId);
        }
        else
        {
            _changeJournal.AppendTurn(version, turnUpsert);
        }

        completed.TrySetResult(version);
    }

    public async Task<LocalSessionSnapshot?> GetCurrentAsync(
        IReadOnlyCollection<MemoryOutboundPreparedConsent> preparedConsents,
        CancellationToken cancellationToken = default)
    {
        var current = await sessionStore.GetCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
        return current is null
            ? null
            : await BuildSnapshotAsync(current, preparedConsents, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalSessionSnapshot?> WaitForChangeAsync(
        long knownChangeVersion,
        TimeSpan maximumWait,
        IReadOnlyCollection<MemoryOutboundPreparedConsent> preparedConsents,
        CancellationToken cancellationToken = default)
    {
        ValidateWait(maximumWait);
        Task<long>? wait = null;
        if (knownChangeVersion < 0)
        {
            var versionBeforeLookup = ChangeVersion;
            var current = await GetCurrentAsync(preparedConsents, cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                return current;
            }

            lock (_changeGate)
            {
                if (versionBeforeLookup == _changeVersion)
                {
                    wait = _nextChange.Task;
                }
            }
        }

        lock (_changeGate)
        {
            if (wait is null && knownChangeVersion == _changeVersion)
            {
                wait = _nextChange.Task;
            }
        }

        await WaitBoundedAsync(wait, maximumWait, cancellationToken).ConfigureAwait(false);
        return await GetCurrentAsync(preparedConsents, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalSessionProjectionUpdate> WaitForProjectionAsync(
        string coordinatorInstanceId,
        DateTimeOffset coordinatorStartedAtUtc,
        Guid sessionId,
        long knownChangeVersion,
        long knownMessageSequenceNumber,
        TimeSpan maximumWait,
        CancellationToken cancellationToken = default)
    {
        ValidateWait(maximumWait);
        if (!string.Equals(coordinatorInstanceId, CoordinatorInstanceId, StringComparison.Ordinal)
            || coordinatorStartedAtUtc != CoordinatorStartedAtUtc)
        {
            return ResetProjection(sessionId, "coordinator_changed");
        }

        var currentSession = await sessionStore.GetCurrentSessionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (currentSession is null || currentSession.Id != sessionId)
        {
            return ResetProjection(currentSession?.Id, "session_changed");
        }

        var versionBeforeWait = ChangeVersion;
        if (knownChangeVersion == versionBeforeWait)
        {
            var pending = await conversationStore.GetMessageChangesAsync(
                    currentSession.ConversationId,
                    knownMessageSequenceNumber,
                    50,
                    cancellationToken)
                .ConfigureAwait(false);
            if (pending.Items.Count > 0)
            {
                return MessageOnlyUpdate(sessionId, versionBeforeWait, pending);
            }

            Task<long>? wait = null;
            lock (_changeGate)
            {
                if (knownChangeVersion == _changeVersion)
                {
                    wait = _nextChange.Task;
                }
            }

            await WaitBoundedAsync(wait, maximumWait, cancellationToken).ConfigureAwait(false);
        }

        var currentVersion = ChangeVersion;
        if (knownChangeVersion < 0 || knownChangeVersion > currentVersion)
        {
            return ResetProjection(sessionId, "change_version_invalid");
        }

        var journal = knownChangeVersion == currentVersion
            ? new SessionJournalReadResult(false, null, [])
            : _changeJournal.Read(knownChangeVersion, currentVersion, sessionId);
        if (journal.ResetRequired)
        {
            return ResetProjection(sessionId, journal.ResetReason ?? "journal_gap");
        }

        var messageChanges = await conversationStore.GetMessageChangesAsync(
                currentSession.ConversationId,
                knownMessageSequenceNumber,
                50,
                cancellationToken)
            .ConfigureAwait(false);
        if (messageChanges.HasMore)
        {
            return ResetProjection(sessionId, "message_upsert_overflow");
        }

        if (knownChangeVersion == currentVersion
            && journal.TurnUpserts.Count == 0
            && messageChanges.Items.Count == 0)
        {
            return new LocalSessionProjectionUpdate(
                LocalSessionProjectionKind.NoChange,
                currentVersion,
                CoordinatorInstanceId,
                CoordinatorStartedAtUtc,
                sessionId,
                [],
                [],
                knownMessageSequenceNumber);
        }

        return new LocalSessionProjectionUpdate(
            LocalSessionProjectionKind.Delta,
            currentVersion,
            CoordinatorInstanceId,
            CoordinatorStartedAtUtc,
            sessionId,
            journal.TurnUpserts,
            messageChanges.Items,
            messageChanges.LastSequenceNumber);
    }

    public async Task<LocalSessionMessagesPage> GetMessagesPageAsync(
        Guid sessionId,
        long? beforeSequenceNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var session = await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var page = await conversationStore.GetMessagesPageAsync(
                session.ConversationId,
                beforeSequenceNumber,
                pageSize,
                cancellationToken)
            .ConfigureAwait(false);
        return new LocalSessionMessagesPage(
            CoordinatorInstanceId,
            CoordinatorStartedAtUtc,
            sessionId,
            page);
    }

    public async Task<LocalSessionTurnDetails> GetTurnDetailsAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var turn = await sessionStore.GetTurnAsync(turnId, cancellationToken).ConfigureAwait(false);
        if (turn is null || turn.SessionId != sessionId)
        {
            throw new SessionProjectionException(
                "session_turn_not_found",
                "没有找到这条会话请求。");
        }

        return new LocalSessionTurnDetails(
            CoordinatorInstanceId,
            CoordinatorStartedAtUtc,
            sessionId,
            turn);
    }

    public async Task<LocalSessionSnapshot> BuildSnapshotAsync(
        SessionRecord session,
        IReadOnlyCollection<MemoryOutboundPreparedConsent> preparedConsents,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var before = ChangeVersion;
            var latestSessionTask = sessionStore.GetSessionAsync(session.Id, cancellationToken);
            var turnsTask = sessionStore.GetTurnsPageAsync(
                session.Id,
                beforeSequenceNumber: null,
                pageSize: SessionTurnProjection.MaximumTurnViews,
                cancellationToken);
            var activeTask = sessionStore.GetActiveTurnsAsync(
                session.Id,
                SessionTurnProjection.MaximumTurnViews,
                cancellationToken);
            var messagesTask = conversationStore.GetMessagesPageAsync(
                session.ConversationId,
                beforeSequenceNumber: null,
                pageSize: 50,
                cancellationToken);
            var projectsTask = tasks.GetAuthorizedProjectsAsync(cancellationToken);
            await Task.WhenAll(latestSessionTask, turnsTask, activeTask, messagesTask, projectsTask)
                .ConfigureAwait(false);
            var after = ChangeVersion;
            if (before != after && attempt < 3)
            {
                continue;
            }

            var latestSession = await latestSessionTask.ConfigureAwait(false) ?? session;
            var messages = await messagesTask.ConfigureAwait(false);
            var projectedTurns = SessionTurnProjection.Select(
                (await turnsTask.ConfigureAwait(false)).Items,
                await activeTask.ConfigureAwait(false));
            var selectedName = latestSession.SelectedProjectId is { } selectedProjectId
                ? (await projectsTask.ConfigureAwait(false))
                    .SingleOrDefault(project => project.Id == selectedProjectId)?.Name
                : null;
            return new LocalSessionSnapshot(
                after,
                CoordinatorInstanceId,
                CoordinatorStartedAtUtc,
                latestSession,
                selectedName,
                projectedTurns.Turns,
                projectedTurns.AdditionalActiveTurns,
                messages.Items,
                preparedConsents
                    .Where(item => item.SessionId == session.Id
                                   && projectedTurns.SelectedActiveTurns.Any(turn => turn.Id == item.TurnId
                                       && SessionTurnPhases.IsForegroundWork(turn.Phase)))
                    .OrderBy(item => item.PreparedAtUtc)
                    .ToArray(),
                messages.HasMore);
        }

        throw new InvalidOperationException("会话状态更新过于频繁，请稍后重试。 ");
    }

    private LocalSessionProjectionUpdate MessageOnlyUpdate(
        Guid sessionId,
        long version,
        ConversationMessageChangeBatch changes) => changes.HasMore
        ? ResetProjection(sessionId, "message_upsert_overflow")
        : new LocalSessionProjectionUpdate(
            LocalSessionProjectionKind.Delta,
            version,
            CoordinatorInstanceId,
            CoordinatorStartedAtUtc,
            sessionId,
            [],
            changes.Items,
            changes.LastSequenceNumber);

    private LocalSessionProjectionUpdate ResetProjection(Guid? sessionId, string reason) => new(
        LocalSessionProjectionKind.ResetRequired,
        ChangeVersion,
        CoordinatorInstanceId,
        CoordinatorStartedAtUtc,
        sessionId,
        [],
        [],
        0,
        reason);

    private async Task<SessionRecord> RequireSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken) =>
        await sessionStore.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("会话不存在。");

    private static void ValidateWait(TimeSpan maximumWait)
    {
        if (maximumWait < TimeSpan.Zero || maximumWait > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWait));
        }
    }

    private static async Task WaitBoundedAsync(
        Task<long>? wait,
        TimeSpan maximumWait,
        CancellationToken cancellationToken)
    {
        if (wait is null)
        {
            return;
        }

        try
        {
            await wait.WaitAsync(maximumWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    private static TaskCompletionSource<long> NewChangeSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
