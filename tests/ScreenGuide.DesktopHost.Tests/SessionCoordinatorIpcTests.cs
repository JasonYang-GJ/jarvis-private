using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.Core.Conversations;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class SessionCoordinatorIpcTests
{
    [Fact]
    public async Task TenInputsContinueOneSessionAndOneProviderThread()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new StatefulSessionProvider();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IConversationProvider>(provider));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("十轮连续对话");
        string[] inputs =
        [
            "我准备开发一个 AI 助手。",
            "你觉得它第一步应该解决什么？",
            "给我两个方案。",
            "第二个详细一点。",
            "继续。",
            "刚才那个风险是什么？",
            "不是这个，我说的是第二个方案。",
            "把它变成三步。",
            "引用我前面说的 AI 助手目标。",
            "最后给出第一步。"
        ];

        foreach (var input in inputs)
        {
            var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
                input,
                "Text",
                $"ten-turn-{Guid.NewGuid():N}",
                session.SessionId));
            await WaitForTerminalTurnAsync(client, submitted.TurnId, TimeSpan.FromSeconds(5));
        }

        var snapshot = await client.GetCurrentSessionAsync();
        await host.StopAsync();

        Assert.NotNull(snapshot);
        Assert.Equal(session.SessionId, snapshot.SessionId);
        Assert.Equal(10, snapshot.Turns.Count);
        Assert.All(snapshot.Turns, turn => Assert.Equal("Completed", turn.Phase));
        Assert.Equal(20, snapshot.Messages.Count);
        Assert.Equal(10, provider.Requests.Count);
        Assert.Null(provider.Requests[0].ExternalThreadId);
        Assert.All(provider.Requests.Skip(1), request =>
            Assert.Equal("thread-unified-session", request.ExternalThreadId));
    }

    [Fact]
    public async Task NewInputCancelsTheOldProviderTurnBeforeItCanPublishALateReply()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new BargeInSessionProvider();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IConversationProvider>(provider));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("打断测试");
        var first = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "请给我一个很长的回答。",
            "Voice",
            "barge-first",
            session.SessionId));
        await provider.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var second = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "停，先别说这个。只回答新的指令。",
            "Voice",
            "barge-second",
            session.SessionId));
        await WaitForTerminalTurnAsync(client, second.TurnId, TimeSpan.FromSeconds(5));
        var snapshot = await client.GetCurrentSessionAsync();
        await host.StopAsync();

        Assert.NotNull(snapshot);
        Assert.Equal("Cancelled", snapshot.Turns.Single(turn => turn.Id == first.TurnId).Phase);
        Assert.Equal("Completed", snapshot.Turns.Single(turn => turn.Id == second.TurnId).Phase);
        Assert.Equal(3, snapshot.Messages.Count);
        Assert.DoesNotContain(snapshot.Messages, message =>
            message.Content.Contains("旧回答", StringComparison.Ordinal));
        Assert.Equal("新的回答", snapshot.Messages[^1].Content);
        Assert.True(provider.CancellationObserved);
        Assert.Equal(1, provider.CancelCount);
    }

    private static async Task<SessionSnapshotDto> WaitForTerminalTurnAsync(
        IDesktopApiClient client,
        Guid turnId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await client.GetCurrentSessionAsync();
            var turn = snapshot?.Turns.SingleOrDefault(item => item.Id == turnId);
            if (turn is not null && turn.Phase is "Completed" or "Failed" or "Cancelled" or "Interrupted")
            {
                return snapshot!;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("统一会话 Turn 未在预期时间内结束。");
    }

    private sealed class StatefulSessionProvider : IConversationProvider
    {
        public string ProviderId => "stateful-session-test";

        public List<ConversationProviderRequest> Requests { get; } = [];

        public async Task<ConversationProviderResult> SendAsync(
            ConversationProviderRequest request,
            Func<string, int, Task>? started = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (started is not null)
            {
                await started("thread-unified-session", 6000 + Requests.Count);
            }

            return new ConversationProviderResult(
                ConversationProviderOutcome.Succeeded,
                "thread-unified-session",
                $"第 {Requests.Count} 轮回答",
                $"session-message-{Requests.Count}",
                6000 + Requests.Count);
        }

        public Task CancelAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BargeInSessionProvider : IConversationProvider
    {
        private int _requestCount;

        public string ProviderId => "barge-in-session-test";

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        public int CancelCount { get; private set; }

        public async Task<ConversationProviderResult> SendAsync(
            ConversationProviderRequest request,
            Func<string, int, Task>? started = null,
            CancellationToken cancellationToken = default)
        {
            var number = Interlocked.Increment(ref _requestCount);
            if (started is not null)
            {
                await started("thread-barge-in", 7000 + number);
            }

            if (number == 1)
            {
                FirstStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved = true;
                }

                return new ConversationProviderResult(
                    ConversationProviderOutcome.Succeeded,
                    "thread-barge-in",
                    "不应该出现的旧回答",
                    "old-message",
                    7001);
            }

            return new ConversationProviderResult(
                ConversationProviderOutcome.Succeeded,
                "thread-barge-in",
                "新的回答",
                "new-message",
                7002);
        }

        public Task CancelAsync(Guid conversationId, CancellationToken cancellationToken = default)
        {
            CancelCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
