using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Services;

public sealed class SessionProjectionCache
{
    private const int MaximumCachedTurns = 32;
    private const int MaximumCachedMessages = 200;

    public SessionSnapshotDto? Snapshot { get; private set; }

    public long? NextBeforeMessageSequenceNumber { get; private set; }

    public bool HasEarlierMessages { get; private set; }

    public bool Reset(SessionSnapshotDto snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!SessionUiPresenter.ShouldApply(Snapshot, snapshot))
        {
            return false;
        }

        var bounded = SelectTurns(
            snapshot.Turns,
            snapshot.ActiveTurns.Concat(
                snapshot.ForegroundTurn is null ? [] : [snapshot.ForegroundTurn]));
        Snapshot = snapshot with
        {
            ForegroundTurn = bounded.ForegroundTurn,
            ActiveTurns = bounded.AdditionalActiveTurns,
            Turns = bounded.Turns,
            Messages = DeduplicateMessages(snapshot.Messages)
                .OrderByDescending(item => item.SequenceNumber)
                .Take(MaximumCachedMessages)
                .OrderBy(item => item.SequenceNumber)
                .ToArray()
        };
        NextBeforeMessageSequenceNumber = Snapshot.Messages.FirstOrDefault()?.SequenceNumber;
        HasEarlierMessages = snapshot.HasEarlierMessages;
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
            || update.ChangeVersion < current.ChangeVersion
            || (update.ChangeVersion == current.ChangeVersion
                && (update.TurnUpserts.Count > 0
                    || update.MessageUpserts.All(message =>
                        current.Messages.Any(existing =>
                            existing.Id == message.Id
                            && existing.SequenceNumber == message.SequenceNumber)))))
        {
            return false;
        }

        var turns = current.Turns.ToDictionary(item => item.Id);
        var additionalActive = current.ActiveTurns.ToDictionary(item => item.Id);
        foreach (var turn in update.TurnUpserts)
        {
            turns[turn.Id] = turn;
            additionalActive.Remove(turn.Id);
        }

        var bounded = SelectTurns(turns.Values, additionalActive.Values);
        var activeTurns = bounded.Turns
            .Concat(bounded.AdditionalActiveTurns)
            .Where(item => !IsTerminal(item.Phase))
            .ToArray();
        var messages = DeduplicateMessages(current.Messages.Concat(update.MessageUpserts))
            .OrderByDescending(item => item.SequenceNumber)
            .Take(MaximumCachedMessages)
            .OrderBy(item => item.SequenceNumber)
            .ToArray();
        var status = bounded.ForegroundTurn?.Phase
                     ?? activeTurns.OrderBy(item => item.SequenceNumber).ThenBy(item => item.Id).LastOrDefault()?.Phase
                     ?? bounded.Turns.LastOrDefault()?.Phase
                     ?? "Ready";
        Snapshot = current with
        {
            ChangeVersion = update.ChangeVersion,
            Status = status,
            ForegroundTurn = bounded.ForegroundTurn,
            ActiveTurns = bounded.AdditionalActiveTurns,
            Turns = bounded.Turns,
            Messages = messages
        };
        return true;
    }

    public bool ApplyEarlierMessages(SessionMessagesPageDto page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var current = Snapshot;
        if (current is null
            || page.SessionId != current.SessionId
            || !string.Equals(
                page.CoordinatorInstanceId,
                current.CoordinatorInstanceId,
                StringComparison.Ordinal)
            || page.CoordinatorStartedAtUtc != current.CoordinatorStartedAtUtc)
        {
            return false;
        }

        var messages = DeduplicateMessages(current.Messages.Concat(page.Messages))
            .OrderBy(item => item.SequenceNumber)
            .Take(MaximumCachedMessages)
            .ToArray();
        Snapshot = current with
        {
            Messages = messages,
            HasEarlierMessages = page.HasMore
        };
        NextBeforeMessageSequenceNumber = page.NextBeforeSequenceNumber;
        HasEarlierMessages = page.HasMore;
        return true;
    }

    private static BoundedTurnView SelectTurns(
        IEnumerable<UnifiedSessionTurnDto> recentTurns,
        IEnumerable<UnifiedSessionTurnDto> additionalActiveTurns)
    {
        var recent = recentTurns
            .GroupBy(item => item.Id)
            .Select(group => group.OrderByDescending(item => item.SequenceNumber).First())
            .ToArray();
        var recentIds = recent.Select(item => item.Id).ToHashSet();
        var candidates = recent.ToDictionary(item => item.Id);
        foreach (var turn in additionalActiveTurns)
        {
            if (!IsTerminal(turn.Phase))
            {
                candidates[turn.Id] = turn;
            }
        }

        var foreground = candidates.Values
            .Where(item => !IsTerminal(item.Phase) && IsForeground(item.Phase))
            .OrderByDescending(item => item.SequenceNumber)
            .ThenByDescending(item => item.UpdatedAtUtc)
            .ThenBy(item => item.Id)
            .FirstOrDefault();
        var selected = candidates.Values
            .OrderBy(item => item.Id == foreground?.Id ? 0 : !IsTerminal(item.Phase) ? 1 : 2)
            .ThenByDescending(item => item.SequenceNumber)
            .ThenByDescending(item => item.UpdatedAtUtc)
            .ThenBy(item => item.Id)
            .Take(MaximumCachedTurns)
            .ToArray();
        var turns = selected
            .Where(item => recentIds.Contains(item.Id))
            .OrderBy(item => item.SequenceNumber)
            .ThenBy(item => item.Id)
            .ToArray();
        var active = selected
            .Where(item => !IsTerminal(item.Phase) && !recentIds.Contains(item.Id))
            .OrderBy(item => item.SequenceNumber)
            .ThenBy(item => item.Id)
            .ToArray();
        return new BoundedTurnView(turns, active, foreground);
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

    private sealed record BoundedTurnView(
        IReadOnlyList<UnifiedSessionTurnDto> Turns,
        IReadOnlyList<UnifiedSessionTurnDto> AdditionalActiveTurns,
        UnifiedSessionTurnDto? ForegroundTurn);
}
