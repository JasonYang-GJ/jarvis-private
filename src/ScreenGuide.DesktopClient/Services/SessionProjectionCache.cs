using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Services;

public sealed class SessionProjectionCache
{
    private const int MaximumCachedTurns = 32;
    private const int MaximumCachedMessages = 200;

    public SessionSnapshotDto? Snapshot { get; private set; }

    public bool Reset(SessionSnapshotDto snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!SessionUiPresenter.ShouldApply(Snapshot, snapshot))
        {
            return false;
        }

        Snapshot = snapshot with
        {
            Turns = snapshot.Turns
                .OrderByDescending(item => item.SequenceNumber)
                .Take(MaximumCachedTurns)
                .OrderBy(item => item.SequenceNumber)
                .ToArray(),
            Messages = DeduplicateMessages(snapshot.Messages)
                .OrderByDescending(item => item.SequenceNumber)
                .Take(MaximumCachedMessages)
                .OrderBy(item => item.SequenceNumber)
                .ToArray()
        };
        return true;
    }

    public bool Apply(SessionProjectionUpdateDto update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var current = Snapshot;
        if (current is null
            || !string.Equals(update.Kind, "Delta", StringComparison.Ordinal)
            || update.Bootstrap is not null
            || update.SessionId != current.SessionId
            || !string.Equals(
                update.CoordinatorInstanceId,
                current.CoordinatorInstanceId,
                StringComparison.Ordinal)
            || update.CoordinatorStartedAtUtc != current.CoordinatorStartedAtUtc
            || update.ChangeVersion <= current.ChangeVersion)
        {
            return false;
        }

        var turns = current.Turns.ToDictionary(item => item.Id);
        foreach (var turn in update.TurnUpserts)
        {
            turns[turn.Id] = turn;
        }

        var boundedTurns = turns.Values
            .OrderByDescending(item => item.SequenceNumber)
            .Take(MaximumCachedTurns)
            .OrderBy(item => item.SequenceNumber)
            .ToArray();
        var activeTurns = boundedTurns.Where(item => !IsTerminal(item.Phase)).ToArray();
        var foreground = activeTurns
            .Where(item => IsForeground(item.Phase))
            .OrderBy(item => item.SequenceNumber)
            .LastOrDefault();
        var messages = DeduplicateMessages(current.Messages.Concat(update.MessageUpserts))
            .OrderByDescending(item => item.SequenceNumber)
            .Take(MaximumCachedMessages)
            .OrderBy(item => item.SequenceNumber)
            .ToArray();
        var status = foreground?.Phase
                     ?? activeTurns.LastOrDefault()?.Phase
                     ?? boundedTurns.LastOrDefault()?.Phase
                     ?? "Ready";
        Snapshot = current with
        {
            ChangeVersion = update.ChangeVersion,
            Status = status,
            ForegroundTurn = foreground,
            ActiveTurns = activeTurns,
            Turns = boundedTurns,
            Messages = messages
        };
        return true;
    }

    private static IReadOnlyList<ConversationMessageDto> DeduplicateMessages(
        IEnumerable<ConversationMessageDto> messages)
    {
        var seen = new HashSet<(Guid Id, long SequenceNumber)>();
        return messages
            .Where(item => seen.Add((item.Id, item.SequenceNumber)))
            .ToArray();
    }

    private static bool IsTerminal(string phase) => phase is
        "Completed" or "Failed" or "Cancelled" or "Interrupted";

    private static bool IsForeground(string phase) => phase is
        "Understanding" or "Responding" or "WaitingForProject" or "WaitingForFile"
        or "WaitingForWindow" or "WaitingForWindowConsent" or "WaitingForConfirmation"
        or "WaitingForMemoryOutboundConsent" or "Executing" or "ObservingWindow";
}
