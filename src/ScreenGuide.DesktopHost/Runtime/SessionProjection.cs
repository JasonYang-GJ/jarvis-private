using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Sessions;

namespace ScreenGuide.DesktopHost.Runtime;

public enum LocalSessionProjectionKind
{
    NoChange,
    Delta,
    ResetRequired
}

public sealed record LocalSessionProjectionUpdate(
    LocalSessionProjectionKind Kind,
    long ChangeVersion,
    string CoordinatorInstanceId,
    DateTimeOffset CoordinatorStartedAtUtc,
    Guid? SessionId,
    IReadOnlyList<SessionTurnRecord> TurnUpserts,
    IReadOnlyList<ConversationMessageRecord> MessageUpserts,
    long LastMessageSequenceNumber,
    string? ResetReason = null);

public sealed record LocalSessionMessagesPage(
    string CoordinatorInstanceId,
    DateTimeOffset CoordinatorStartedAtUtc,
    Guid SessionId,
    ConversationMessagePage Page);

public sealed record LocalSessionTurnDetails(
    string CoordinatorInstanceId,
    DateTimeOffset CoordinatorStartedAtUtc,
    Guid SessionId,
    SessionTurnRecord Turn);

public sealed class SessionProjectionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal sealed record BoundedSessionTurnProjection(
    IReadOnlyList<SessionTurnRecord> Turns,
    IReadOnlyList<SessionTurnRecord> AdditionalActiveTurns,
    IReadOnlyList<SessionTurnRecord> SelectedActiveTurns);

internal static class SessionTurnProjection
{
    public const int MaximumTurnViews = 32;

    public static BoundedSessionTurnProjection Select(
        IReadOnlyList<SessionTurnRecord> recentTurns,
        IReadOnlyList<SessionTurnRecord> activeTurns)
    {
        var recentIds = recentTurns.Select(item => item.Id).ToHashSet();
        var activeIds = activeTurns.Select(item => item.Id).ToHashSet();
        var candidates = new Dictionary<Guid, SessionTurnRecord>();
        foreach (var turn in recentTurns)
        {
            candidates[turn.Id] = turn;
        }

        foreach (var turn in activeTurns)
        {
            candidates[turn.Id] = turn;
        }

        var foreground = activeTurns
            .Where(item => SessionTurnPhases.IsForegroundWork(item.Phase))
            .OrderByDescending(item => item.SequenceNumber)
            .ThenByDescending(item => item.UpdatedAtUtc)
            .ThenBy(item => item.Id)
            .FirstOrDefault();
        var selected = candidates.Values
            .OrderBy(item => item.Id == foreground?.Id ? 0 : activeIds.Contains(item.Id) ? 1 : 2)
            .ThenByDescending(item => item.SequenceNumber)
            .ThenByDescending(item => item.UpdatedAtUtc)
            .ThenBy(item => item.Id)
            .Take(MaximumTurnViews)
            .ToArray();
        var selectedIds = selected.Select(item => item.Id).ToHashSet();
        var turns = selected
            .Where(item => recentIds.Contains(item.Id))
            .OrderBy(item => item.SequenceNumber)
            .ThenBy(item => item.Id)
            .ToArray();
        var additionalActive = activeTurns
            .Where(item => selectedIds.Contains(item.Id) && !recentIds.Contains(item.Id))
            .OrderBy(item => item.SequenceNumber)
            .ThenBy(item => item.Id)
            .ToArray();
        var selectedActive = activeTurns
            .Where(item => selectedIds.Contains(item.Id))
            .OrderBy(item => item.SequenceNumber)
            .ThenBy(item => item.Id)
            .ToArray();
        return new BoundedSessionTurnProjection(turns, additionalActive, selectedActive);
    }
}

internal sealed record SessionJournalReadResult(
    bool ResetRequired,
    string? ResetReason,
    IReadOnlyList<SessionTurnRecord> TurnUpserts);

internal sealed class SessionChangeJournal(int capacity = 256)
{
    private readonly object _gate = new();
    private readonly Queue<Entry> _entries = new();

    public void AppendReset(long version, Guid? sessionId)
    {
        lock (_gate)
        {
            AppendCore(new Entry(version, sessionId, null, true));
        }
    }

    public void AppendTurn(long version, SessionTurnRecord turn)
    {
        lock (_gate)
        {
            AppendCore(new Entry(version, turn.SessionId, turn, false));
        }
    }

    public SessionJournalReadResult Read(long knownVersion, long currentVersion, Guid sessionId)
    {
        lock (_gate)
        {
            if (knownVersion >= currentVersion)
            {
                return new SessionJournalReadResult(false, null, []);
            }

            var relevant = _entries.Where(item => item.Version > knownVersion).ToArray();
            if (relevant.Length == 0 || relevant[0].Version > knownVersion + 1)
            {
                return new SessionJournalReadResult(true, "journal_gap", []);
            }

            if (relevant[^1].Version != currentVersion)
            {
                return new SessionJournalReadResult(true, "journal_gap", []);
            }

            if (relevant.Any(item => item.RequiresReset || item.SessionId != sessionId))
            {
                return new SessionJournalReadResult(true, "projection_invalidated", []);
            }

            var upserts = relevant
                .Where(item => item.Turn is not null)
                .GroupBy(item => item.Turn!.Id)
                .Select(group => group.OrderByDescending(item => item.Version).First().Turn!)
                .OrderBy(item => item.SequenceNumber)
                .ToArray();
            return upserts.Length > 32
                ? new SessionJournalReadResult(true, "turn_upsert_overflow", [])
                : new SessionJournalReadResult(false, null, upserts);
        }
    }

    private void AppendCore(Entry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > capacity)
        {
            _entries.Dequeue();
        }
    }

    private sealed record Entry(
        long Version,
        Guid? SessionId,
        SessionTurnRecord? Turn,
        bool RequiresReset);
}
