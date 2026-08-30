using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class SessionProjectionCacheTests
{
    [Fact]
    public void AppliesBoundedUpsertsDeduplicatesMessagesAndRejectsOldResponses()
    {
        var sessionId = Guid.NewGuid();
        var originalTurn = Turn(1, "Understanding");
        var firstMessage = Message(1, "first");
        var bootstrap = Snapshot(sessionId, 5, [originalTurn], [firstMessage]);
        var cache = new SessionProjectionCache();

        Assert.True(cache.Reset(bootstrap));
        var completedTurn = originalTurn with { Phase = "Completed", ResultSummary = "done" };
        var secondMessage = Message(2, "second");
        var delta = new SessionProjectionUpdateDto(
            "Delta",
            6,
            bootstrap.CoordinatorInstanceId,
            bootstrap.CoordinatorStartedAtUtc,
            sessionId,
            [completedTurn],
            [firstMessage, secondMessage, secondMessage],
            2);

        Assert.True(cache.Apply(delta));
        Assert.Equal("Completed", cache.Snapshot!.Turns.Single().Phase);
        Assert.Equal([1L, 2L], cache.Snapshot.Messages.Select(item => item.SequenceNumber));
        Assert.False(cache.Apply(delta with { ChangeVersion = 4 }));
        Assert.False(cache.Apply(delta with { SessionId = Guid.NewGuid(), ChangeVersion = 7 }));
        Assert.Equal(6, cache.Snapshot.ChangeVersion);
    }

    [Fact]
    public void RestartedCoordinatorRequiresBootstrapAndResetRequiredNeverMutatesTheCache()
    {
        var bootstrap = Snapshot(Guid.NewGuid(), 80, [Turn(1, "Responding")], [Message(1, "one")]);
        var cache = new SessionProjectionCache();
        Assert.True(cache.Reset(bootstrap));

        var reset = new SessionProjectionUpdateDto(
            "ResetRequired",
            2,
            "new-instance",
            bootstrap.CoordinatorStartedAtUtc.AddMinutes(1),
            bootstrap.SessionId,
            [],
            [],
            0,
            "coordinator_changed");

        Assert.False(cache.Apply(reset));
        Assert.Equal(bootstrap.CoordinatorInstanceId, cache.Snapshot!.CoordinatorInstanceId);

        var restarted = bootstrap with
        {
            CoordinatorInstanceId = "new-instance",
            CoordinatorStartedAtUtc = bootstrap.CoordinatorStartedAtUtc.AddMinutes(1),
            ChangeVersion = 2,
            Turns = [Turn(1, "Interrupted")],
            ActiveTurns = []
        };
        Assert.True(cache.Reset(restarted));
        Assert.Equal("Interrupted", cache.Snapshot!.Turns.Single().Phase);
    }

    private static SessionSnapshotDto Snapshot(
        Guid sessionId,
        long changeVersion,
        UnifiedSessionTurnDto[] turns,
        ConversationMessageDto[] messages) => new(
        changeVersion,
        "projection-cache-instance",
        new DateTimeOffset(2026, 8, 30, 3, 0, 0, TimeSpan.Zero),
        sessionId,
        Guid.NewGuid(),
        "缓存测试",
        turns.LastOrDefault()?.Phase ?? "Ready",
        null,
        null,
        turns.LastOrDefault(),
        turns.Where(item => item.Phase is not "Completed" and not "Failed" and not "Cancelled" and not "Interrupted").ToArray(),
        turns,
        messages);

    private static UnifiedSessionTurnDto Turn(int sequence, string phase) => new(
        Guid.NewGuid(),
        sequence,
        $"turn-{sequence}",
        "Text",
        "Conversation",
        phase,
        "None",
        "Conversation",
        null,
        null,
        null,
        null,
        null,
        null,
        false,
        false,
        null,
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        null);

    private static ConversationMessageDto Message(long sequence, string content) => new(
        Guid.NewGuid(),
        sequence,
        "User",
        content,
        DateTimeOffset.UtcNow);
}
