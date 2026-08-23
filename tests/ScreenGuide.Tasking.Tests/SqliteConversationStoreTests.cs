using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Tasking;
using ScreenGuide.Persistence.Sqlite;

namespace ScreenGuide.Tasking.Tests;

public sealed class SqliteConversationStoreTests
{
    [Fact]
    public async Task ConversationTurnIsAtomicPersistentAndIdempotent()
    {
        await using var environment = await ConversationStoreEnvironment.CreateAsync();
        var conversation = await environment.CreateConversationAsync();
        var now = environment.Now;

        var first = await environment.Store.StartTurnAsync(
            conversation.Id,
            Guid.NewGuid(),
            "你好",
            "fixed-key",
            now);
        await environment.Store.RecordProviderStartedAsync(
            conversation.Id,
            first.Turn.Id,
            "thread-1",
            1234,
            now);
        await environment.Store.CompleteTurnAsync(
            conversation.Id,
            first.Turn.Id,
            "你好，我是元枢。",
            "assistant-1",
            now.AddSeconds(2));
        var duplicate = await environment.Store.StartTurnAsync(
            conversation.Id,
            Guid.NewGuid(),
            "你好",
            "fixed-key",
            now.AddSeconds(3));

        var stored = await environment.Store.GetConversationAsync(conversation.Id);
        var messages = await environment.Store.GetMessagesAsync(conversation.Id);
        var turns = await environment.Store.GetTurnsAsync(conversation.Id);

        Assert.True(first.Accepted);
        Assert.False(duplicate.Accepted);
        Assert.Equal(first.Turn.Id, duplicate.Turn.Id);
        Assert.Equal("thread-1", stored?.ExternalThreadId);
        Assert.Equal("你好", stored?.Title);
        Assert.Equal(ConversationStatus.Ready, stored?.Status);
        Assert.Equal(["你好", "你好，我是元枢。"], messages.Select(item => item.Content).ToArray());
        Assert.Equal(ConversationTurnStatus.Succeeded, Assert.Single(turns).Status);
    }

    [Fact]
    public async Task StartupRecoveryInterruptsRunningTurnWithoutReplayingIt()
    {
        await using var environment = await ConversationStoreEnvironment.CreateAsync();
        var conversation = await environment.CreateConversationAsync();
        var turn = await environment.Store.StartTurnAsync(
            conversation.Id,
            Guid.NewGuid(),
            "仍在回答",
            "recovery-key",
            environment.Now);

        var recovery = await environment.Store.RecoverInterruptedAsync(environment.Now.AddMinutes(1));
        var stored = await environment.Store.GetConversationAsync(conversation.Id);
        var storedTurn = Assert.Single(await environment.Store.GetTurnsAsync(conversation.Id));

        Assert.Contains(conversation.Id, recovery.InterruptedConversationIds);
        Assert.Equal(ConversationStatus.Interrupted, stored?.Status);
        Assert.Equal(ConversationTurnStatus.Interrupted, storedTurn.Status);
        Assert.Equal(turn.Turn.Id, storedTurn.Id);
        Assert.Equal("host_restarted", storedTurn.FailureCode);
        Assert.Single(await environment.Store.GetMessagesAsync(conversation.Id));
    }

    [Fact]
    public async Task ASecondTurnIsRejectedWhileTheFirstIsStillRunning()
    {
        await using var environment = await ConversationStoreEnvironment.CreateAsync();
        var conversation = await environment.CreateConversationAsync();
        await environment.Store.StartTurnAsync(
            conversation.Id,
            Guid.NewGuid(),
            "第一条",
            "running-1",
            environment.Now);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            environment.Store.StartTurnAsync(
                conversation.Id,
                Guid.NewGuid(),
                "第二条",
                "running-2",
                environment.Now));

        Assert.Contains("上一条", exception.Message);
    }

    private sealed class ConversationStoreEnvironment : IAsyncDisposable
    {
        private ConversationStoreEnvironment(
            string root,
            SqliteConversationStore store,
            DeviceRecord device)
        {
            Root = root;
            Store = store;
            Device = device;
        }

        public string Root { get; }

        public SqliteConversationStore Store { get; }

        public DeviceRecord Device { get; }

        public DateTimeOffset Now { get; } = new(2026, 8, 19, 8, 0, 0, TimeSpan.Zero);

        public static async Task<ConversationStoreEnvironment> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"screen-guide-conversation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "screen-guide.db");
            await using var taskStore = new SqliteTaskStore(databasePath);
            await taskStore.InitializeAsync();
            var now = new DateTimeOffset(2026, 8, 19, 8, 0, 0, TimeSpan.Zero);
            var device = new DeviceRecord
            {
                Id = Guid.NewGuid(),
                DisplayName = "Conversation Test Host",
                DeviceType = DeviceType.WindowsHost,
                TrustState = DeviceTrustState.Local,
                CreatedAtUtc = now,
                LastSeenAtUtc = now
            };
            await taskStore.UpsertDeviceAsync(device);
            var store = new SqliteConversationStore(databasePath);
            await store.InitializeAsync();
            return new ConversationStoreEnvironment(root, store, device);
        }

        public async Task<ConversationRecord> CreateConversationAsync()
        {
            var conversation = new ConversationRecord
            {
                Id = Guid.NewGuid(),
                CreatedByDeviceId = Device.Id,
                Title = "新对话",
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now
            };
            await Store.CreateConversationAsync(conversation);
            return conversation;
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
