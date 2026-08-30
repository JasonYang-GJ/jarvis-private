using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Sessions;
using ScreenGuide.Persistence.Sqlite;

namespace ScreenGuide.Tasking.Tests;

public sealed class SqliteSessionProjectionQueryTests
{
    [Fact]
    public async Task BoundedPagesRemainStableAcrossOneThousandTurnsAndConcurrentAppend()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        await using var sessions = new SqliteSessionStore(environment.DatabasePath);
        var conversations = new SqliteConversationStore(environment.DatabasePath);
        await sessions.InitializeAsync();
        await conversations.InitializeAsync();

        var now = new DateTimeOffset(2026, 8, 30, 2, 0, 0, TimeSpan.Zero);
        var conversation = new ConversationRecord
        {
            Id = Guid.NewGuid(),
            CreatedByDeviceId = environment.Device.Id,
            Title = "有界投影",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var session = new SessionRecord
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            CreatedByDeviceId = environment.Device.Id,
            Title = conversation.Title,
            IsCurrent = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastActiveAtUtc = now
        };
        await conversations.CreateConversationAsync(conversation);
        await sessions.CreateSessionAsync(session);

        var turnIds = new List<Guid>(capacity: 1_000);
        for (var index = 1; index <= 1_000; index++)
        {
            var conversationTurnId = Guid.NewGuid();
            var at = now.AddSeconds(index);
            var sessionRegistration = await sessions.StartTurnAsync(
                session.Id,
                $"session-{index}",
                "Text",
                $"session-key-{index}",
                ReadyRoute(at),
                at);
            turnIds.Add(sessionRegistration.Turn.Id);
            await conversations.StartTurnAsync(
                conversation.Id,
                conversationTurnId,
                $"message-{index}",
                $"conversation-key-{index}",
                at);
            await conversations.CompleteTurnAsync(
                conversation.Id,
                conversationTurnId,
                $"reply-{index}",
                null,
                at.AddMilliseconds(1));
        }

        var latestTurns = await sessions.GetTurnsPageAsync(session.Id, beforeSequenceNumber: null, pageSize: 32);
        var boundedActiveTurns = await sessions.GetActiveTurnsAsync(session.Id, limit: 32);
        var firstMessages = await conversations.GetMessagesPageAsync(
            conversation.Id,
            beforeSequenceNumber: null,
            pageSize: 50);

        Assert.Equal(32, latestTurns.Items.Count);
        Assert.Equal(969, latestTurns.Items[0].SequenceNumber);
        Assert.Equal(1_000, latestTurns.Items[^1].SequenceNumber);
        Assert.True(latestTurns.HasMore);
        Assert.Equal(32, boundedActiveTurns.Count);
        Assert.Equal(
            Enumerable.Range(969, 32),
            boundedActiveTurns.Select(item => item.SequenceNumber).Order());
        Assert.Equal(50, firstMessages.Items.Count);
        Assert.Equal(1_951, firstMessages.Items[0].SequenceNumber);
        Assert.Equal(2_000, firstMessages.Items[^1].SequenceNumber);
        Assert.True(firstMessages.HasMore);

        var appendedTurnId = Guid.NewGuid();
        await conversations.StartTurnAsync(
            conversation.Id,
            appendedTurnId,
            "concurrent-append",
            "conversation-key-append",
            now.AddHours(1));
        await conversations.CompleteTurnAsync(
            conversation.Id,
            appendedTurnId,
            "concurrent-reply",
            null,
            now.AddHours(1).AddMilliseconds(1));

        var originalSequences = firstMessages.Items.Select(item => item.SequenceNumber).ToHashSet();
        var cursor = firstMessages.NextBeforeSequenceNumber;
        while (cursor is not null)
        {
            var page = await conversations.GetMessagesPageAsync(
                conversation.Id,
                cursor,
                pageSize: 50);
            foreach (var message in page.Items)
            {
                Assert.True(originalSequences.Add(message.SequenceNumber));
            }

            cursor = page.NextBeforeSequenceNumber;
        }

        Assert.Equal(2_000, originalSequences.Count);
        Assert.Equal(Enumerable.Range(1, 2_000).Select(value => (long)value), originalSequences.Order());
        Assert.Equal(
            turnIds[499],
            (await sessions.GetTurnAsync(turnIds[499]))?.Id);
    }

    private static SessionTurnFrozenRoute ReadyRoute(DateTimeOffset frozenAtUtc) => new()
    {
        Status = SessionTurnRouteStatus.Ready,
        ProviderId = "fake",
        ModelId = "fake-model",
        DataDestination = "https://example.invalid",
        SendsDataOffDevice = false,
        FrozenAtUtc = frozenAtUtc
    };
}
