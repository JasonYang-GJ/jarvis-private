using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.Core.Conversations;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Persistence.Sqlite;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class ConversationIpcIntegrationTests
{
    [Fact]
    public async Task ConversationPersistsMessagesAndContinuesTheSameProviderThread()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new RecordingConversationProvider();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IConversationProvider>(provider));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var conversation = await client.CreateConversationAsync("连续对话测试");
        var first = await client.SendConversationMessageAsync(
            conversation.Id,
            "第一轮",
            "conversation-first");
        await host.Services.GetRequiredService<ConversationService>().WaitForTurnAsync(first.TurnId);
        var firstDetails = await client.GetConversationAsync(conversation.Id);

        var second = await client.SendConversationMessageAsync(
            conversation.Id,
            "第二轮",
            "conversation-second");
        await host.Services.GetRequiredService<ConversationService>().WaitForTurnAsync(second.TurnId);
        var secondDetails = await client.GetConversationAsync(conversation.Id);
        var listed = await client.ListConversationsAsync();
        await host.StopAsync();

        Assert.Equal("Ready", firstDetails?.Summary.Status);
        Assert.Equal(["第一轮", "回答 1"], firstDetails!.Messages.Select(item => item.Content).ToArray());
        Assert.Equal("thread-conversation-test", provider.Requests[1].ExternalThreadId);
        Assert.Equal(4, secondDetails?.Messages.Count);
        Assert.Equal("回答 2", secondDetails?.Messages[^1].Content);
        Assert.All(secondDetails?.Turns ?? [], turn => Assert.Equal("Succeeded", turn.Status));
        Assert.Contains(listed, item => item.Id == conversation.Id);
    }

    [Fact]
    public async Task DuplicateMessageCommandDoesNotStartASecondProviderTurn()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new RecordingConversationProvider();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IConversationProvider>(provider));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var conversation = await client.CreateConversationAsync();

        var first = await client.SendConversationMessageAsync(conversation.Id, "只发一次", "same-key");
        await host.Services.GetRequiredService<ConversationService>().WaitForTurnAsync(first.TurnId);
        var duplicate = await client.SendConversationMessageAsync(conversation.Id, "只发一次", "same-key");
        var details = await client.GetConversationAsync(conversation.Id);
        await host.StopAsync();

        Assert.False(first.WasDuplicate);
        Assert.True(duplicate.WasDuplicate);
        Assert.Equal(first.TurnId, duplicate.TurnId);
        Assert.Single(provider.Requests);
        Assert.Equal(2, details?.Messages.Count);
    }

    [Fact]
    public async Task CancelStopsTheActiveConversationWithoutAddingAnAssistantClaim()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new RecordingConversationProvider(blockUntilCancelled: true);
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IConversationProvider>(provider));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var conversation = await client.CreateConversationAsync();
        var sent = await client.SendConversationMessageAsync(conversation.Id, "请等待", "cancel-key");
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await client.CancelConversationTurnAsync(conversation.Id);
        await host.Services.GetRequiredService<ConversationService>().WaitForTurnAsync(sent.TurnId);
        var details = await client.GetConversationAsync(conversation.Id);
        await host.StopAsync();

        Assert.Equal("Ready", details?.Summary.Status);
        Assert.Single(details?.Messages ?? []);
        Assert.Equal("Cancelled", Assert.Single(details?.Turns ?? []).Status);
        Assert.Equal(1, provider.CancelCount);
    }

    [Fact]
    public async Task CancellationReachesTheProviderAndALateReplyCannotBeCommitted()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new CancellationAwareLateReplyProvider();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IConversationProvider>(provider));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var conversation = await client.CreateConversationAsync();
        var sent = await client.SendConversationMessageAsync(conversation.Id, "旧问题", "late-reply-cancel");
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var cancellation = client.CancelConversationTurnAsync(conversation.Id);
        await provider.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        provider.AllowLateReply.TrySetResult();
        await cancellation;
        await host.Services.GetRequiredService<ConversationService>().WaitForTurnAsync(sent.TurnId);
        var details = await client.GetConversationAsync(conversation.Id);
        await host.StopAsync();

        Assert.Equal("Ready", details?.Summary.Status);
        Assert.Equal(["旧问题"], details!.Messages.Select(message => message.Content).ToArray());
        Assert.Equal("Cancelled", Assert.Single(details.Turns).Status);
        Assert.Equal(1, provider.CancelCount);
    }

    [Fact]
    public async Task CancellationWinsBeforeAProviderReplyCanCommitAfterItsTokenCheck()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        await using var barrierStore = new CompletionBarrierConversationStore(
            new SqliteConversationStore(environment.Options.DatabasePath));
        var provider = new RecordingConversationProvider();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IConversationStore>(barrierStore);
            services.AddSingleton<IConversationProvider>(provider);
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var conversation = await client.CreateConversationAsync("完成与取消竞争");
        var sent = await client.SendConversationMessageAsync(
            conversation.Id,
            "旧回答不能在停止后落库",
            "cancel-during-completion");
        await barrierStore.CompletionEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var cancellation = client.CancelConversationTurnAsync(conversation.Id);
        try
        {
            await barrierStore.CancellationPersisted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            barrierStore.AllowCompletion.TrySetResult();
        }

        await cancellation;
        await host.Services.GetRequiredService<ConversationService>().WaitForTurnAsync(sent.TurnId);
        var details = await client.GetConversationAsync(conversation.Id);
        await host.StopAsync();

        Assert.Equal("Ready", details?.Summary.Status);
        Assert.Equal(["旧回答不能在停止后落库"], details!.Messages.Select(message => message.Content).ToArray());
        Assert.Equal("Cancelled", Assert.Single(details.Turns).Status);
        Assert.Equal(1, provider.CancelCount);
    }

    private sealed class RecordingConversationProvider(bool blockUntilCancelled = false) : IConversationProvider
    {
        private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId => "test-conversation";

        public List<ConversationProviderRequest> Requests { get; } = [];

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CancelCount { get; private set; }

        public async Task<ConversationProviderResult> SendAsync(
            ConversationProviderRequest request,
            Func<string, int, Task>? started = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (started is not null)
            {
                await started("thread-conversation-test", 4242);
            }

            Started.TrySetResult();
            if (blockUntilCancelled)
            {
                await _cancelled.Task;
                return new ConversationProviderResult(
                    ConversationProviderOutcome.Cancelled,
                    "thread-conversation-test",
                    null,
                    null,
                    4242,
                    "cancelled",
                    "回答已停止。");
            }

            return new ConversationProviderResult(
                ConversationProviderOutcome.Succeeded,
                "thread-conversation-test",
                $"回答 {Requests.Count}",
                $"message-{Requests.Count}",
                4242);
        }

        public Task CancelAsync(Guid conversationId, CancellationToken cancellationToken = default)
        {
            CancelCount++;
            _cancelled.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _cancelled.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellationAwareLateReplyProvider : IConversationProvider
    {
        public string ProviderId => "cancellation-aware-test";

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowLateReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CancelCount { get; private set; }

        public async Task<ConversationProviderResult> SendAsync(
            ConversationProviderRequest request,
            Func<string, int, Task>? started = null,
            CancellationToken cancellationToken = default)
        {
            if (started is not null)
            {
                await started("thread-late-reply", 5252);
            }

            using var registration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            Started.TrySetResult();
            await AllowLateReply.Task;
            return new ConversationProviderResult(
                ConversationProviderOutcome.Succeeded,
                "thread-late-reply",
                "不应该出现的旧回答",
                "late-message",
                5252);
        }

        public Task CancelAsync(Guid conversationId, CancellationToken cancellationToken = default)
        {
            CancelCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            AllowLateReply.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CompletionBarrierConversationStore(IConversationStore inner)
        : IConversationStore, IAsyncDisposable
    {
        public TaskCompletionSource CompletionEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationPersisted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task CreateConversationAsync(
            ConversationRecord conversation,
            CancellationToken cancellationToken = default) =>
            inner.CreateConversationAsync(conversation, cancellationToken);

        public Task<ConversationRecord?> GetConversationAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default) =>
            inner.GetConversationAsync(conversationId, cancellationToken);

        public Task<IReadOnlyList<ConversationRecord>> GetConversationsAsync(
            CancellationToken cancellationToken = default) =>
            inner.GetConversationsAsync(cancellationToken);

        public Task<IReadOnlyList<ConversationMessageRecord>> GetMessagesAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default) =>
            inner.GetMessagesAsync(conversationId, cancellationToken);

        public Task<IReadOnlyList<ConversationTurnRecord>> GetTurnsAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default) =>
            inner.GetTurnsAsync(conversationId, cancellationToken);

        public Task<ConversationTurnRegistration> StartTurnAsync(
            Guid conversationId,
            Guid turnId,
            string message,
            string idempotencyKey,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken = default) =>
            inner.StartTurnAsync(
                conversationId,
                turnId,
                message,
                idempotencyKey,
                startedAtUtc,
                cancellationToken);

        public Task RecordProviderStartedAsync(
            Guid conversationId,
            Guid turnId,
            string externalThreadId,
            int processId,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken = default) =>
            inner.RecordProviderStartedAsync(
                conversationId,
                turnId,
                externalThreadId,
                processId,
                startedAtUtc,
                cancellationToken);

        public async Task CompleteTurnAsync(
            Guid conversationId,
            Guid turnId,
            string reply,
            string? providerMessageId,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken = default)
        {
            CompletionEntered.TrySetResult();
            await AllowCompletion.Task.WaitAsync(cancellationToken);
            await inner.CompleteTurnAsync(
                conversationId,
                turnId,
                reply,
                providerMessageId,
                completedAtUtc,
                cancellationToken);
        }

        public async Task FailTurnAsync(
            Guid conversationId,
            Guid turnId,
            ConversationTurnStatus status,
            string failureCode,
            string failureMessage,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken = default)
        {
            await inner.FailTurnAsync(
                conversationId,
                turnId,
                status,
                failureCode,
                failureMessage,
                completedAtUtc,
                cancellationToken);
            if (status == ConversationTurnStatus.Cancelled)
            {
                CancellationPersisted.TrySetResult();
            }
        }

        public Task<ConversationRecoveryResult> RecoverInterruptedAsync(
            DateTimeOffset recoveredAtUtc,
            CancellationToken cancellationToken = default) =>
            inner.RecoverInterruptedAsync(recoveredAtUtc, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            AllowCompletion.TrySetResult();
            if (inner is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
        }
    }
}
