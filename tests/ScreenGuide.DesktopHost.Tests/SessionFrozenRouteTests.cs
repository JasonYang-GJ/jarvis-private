using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Sessions;
using ScreenGuide.DesktopHost.Runtime;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class SessionFrozenRouteTests
{
    [Fact]
    public async Task NewTurnsReadSettingsOnceAndADuplicateKeepsTheFirstFrozenRoute()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var providerA = new MetadataOnlyProvider("provider-a", "model-a");
        var providerB = new MetadataOnlyProvider("provider-b", "model-b");
        var settings = new MutableSettingsStore(Route("provider-a", "model-a"));
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(settings);
            services.AddSingleton(new ChatProviderRegistry([providerA, providerB]));
        });
        await host.StartAsync();
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var session = await coordinator.StartNewAsync("冻结路由幂等");

        var first = await coordinator.SubmitAsync(
            session.Session.Id,
            "启动“记事本”",
            "Text",
            "frozen-route-duplicate",
            expectedIntentKind: "OpenApplication",
            expectedTarget: "notepad");
        var firstTurn = await WaitForPhaseAsync(
            store,
            first.TurnId,
            SessionTurnPhase.WaitingForConfirmation);

        await settings.SaveAsync(Route("provider-b", "model-b"));
        var duplicate = await coordinator.SubmitAsync(
            session.Session.Id,
            "启动“记事本”",
            "Text",
            "frozen-route-duplicate",
            expectedIntentKind: "OpenApplication",
            expectedTarget: "notepad");
        var duplicateTurn = await store.GetTurnAsync(duplicate.TurnId);

        await coordinator.CancelTurnAsync(session.Session.Id, first.TurnId);
        var second = await coordinator.SubmitAsync(
            session.Session.Id,
            "启动“记事本”",
            "Text",
            "frozen-route-new-turn",
            expectedIntentKind: "OpenApplication",
            expectedTarget: "notepad");
        var secondTurn = await WaitForPhaseAsync(
            store,
            second.TurnId,
            SessionTurnPhase.WaitingForConfirmation);
        await coordinator.CancelTurnAsync(session.Session.Id, second.TurnId);
        await host.StopAsync();

        Assert.False(first.WasDuplicate);
        Assert.True(duplicate.WasDuplicate);
        Assert.Equal(first.TurnId, duplicate.TurnId);
        Assert.Equal("provider-a", firstTurn.FrozenRoute?.ProviderId);
        Assert.Equal("model-a", firstTurn.FrozenRoute?.ModelId);
        Assert.Equal(firstTurn.FrozenRoute, duplicateTurn?.FrozenRoute);
        Assert.Equal("provider-b", secondTurn.FrozenRoute?.ProviderId);
        Assert.Equal("model-b", secondTurn.FrozenRoute?.ModelId);
        Assert.Equal(2, settings.LoadCount);
        Assert.Equal(0, providerA.CompleteCount);
        Assert.Equal(0, providerB.CompleteCount);
    }

    [Fact]
    public async Task InvalidAiSettingsDoNotBlockDeterministicContextCompletionOrRefreezeTheTurn()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new MetadataOnlyProvider("provider-a", "model-a");
        var settings = new MutableSettingsStore(Route(" ", " "));
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(settings);
            services.AddSingleton(new ChatProviderRegistry([provider]));
        });
        await host.StartAsync();
        var (_, project, _) = await environment.SeedProjectsAsync();
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var session = await coordinator.StartNewAsync("无效设置下补上下文");

        var submitted = await coordinator.SubmitAsync(
            session.Session.Id,
            "把刚才那个问题处理好",
            "ProgrammingTask",
            "invalid-settings-context");
        var waiting = await WaitForPhaseAsync(
            store,
            submitted.TurnId,
            SessionTurnPhase.WaitingForProject);
        var selected = await coordinator.ProvideProjectAsync(
            session.Session.Id,
            submitted.TurnId,
            project.Id);
        var continued = selected.Turns.Single(turn => turn.Id == submitted.TurnId);
        await coordinator.CancelTurnAsync(session.Session.Id, submitted.TurnId);
        await host.StopAsync();

        Assert.Equal(SessionTurnRouteStatus.Unavailable, waiting.FrozenRoute?.Status);
        Assert.Equal("ai_settings_invalid", waiting.FrozenRoute?.FailureCode);
        Assert.Equal(waiting.FrozenRoute, continued.FrozenRoute);
        Assert.Equal(SessionTurnPhase.WaitingForConfirmation, continued.Phase);
        Assert.Equal(1, settings.LoadCount);
        Assert.Equal(0, provider.CompleteCount);
    }

    [Fact]
    public async Task CancellationWhileResolvingTheRouteDoesNotAcceptATurn()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new MetadataOnlyProvider("provider-a", "model-a");
        var settings = new BlockingSettingsStore(Route("provider-a", "model-a"));
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(settings);
            services.AddSingleton(new ChatProviderRegistry([provider]));
        });
        await host.StartAsync();
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var session = await coordinator.StartNewAsync("解析前取消");
        using var cancellation = new CancellationTokenSource();

        var submitting = coordinator.SubmitAsync(
            session.Session.Id,
            "启动“记事本”",
            "Text",
            "cancel-route-resolution",
            cancellation.Token,
            expectedIntentKind: "OpenApplication",
            expectedTarget: "notepad");
        await settings.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submitting);
        Assert.Empty(await store.GetTurnsAsync(session.Session.Id));
        Assert.Equal(1, settings.LoadCount);
        Assert.Equal(0, provider.CompleteCount);
        await host.StopAsync();
    }

    [Fact]
    public async Task ConcurrentDuplicateSubmissionsFreezeAndPersistExactlyOnce()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new MetadataOnlyProvider("provider-a", "model-a");
        var settings = new MutableSettingsStore(Route("provider-a", "model-a"));
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(settings);
            services.AddSingleton(new ChatProviderRegistry([provider]));
        });
        await host.StartAsync();
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var session = await coordinator.StartNewAsync("并发幂等冻结");

        var submissions = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            coordinator.SubmitAsync(
                session.Session.Id,
                "启动“记事本”",
                "Text",
                "concurrent-frozen-route",
                expectedIntentKind: "OpenApplication",
                expectedTarget: "notepad")));
        var turnId = submissions.Select(result => result.TurnId).Distinct().Single();
        var turn = await WaitForPhaseAsync(
            store,
            turnId,
            SessionTurnPhase.WaitingForConfirmation);
        await coordinator.CancelTurnAsync(session.Session.Id, turnId);
        await host.StopAsync();

        Assert.Single(submissions, result => !result.WasDuplicate);
        Assert.Equal(15, submissions.Count(result => result.WasDuplicate));
        Assert.Single(await store.GetTurnsAsync(session.Session.Id));
        Assert.Equal(SessionTurnRouteStatus.Ready, turn.FrozenRoute?.Status);
        Assert.Equal(1, settings.LoadCount);
        Assert.Equal(0, provider.CompleteCount);
    }

    private static AiSettings Route(string providerId, string modelId) =>
        new(new ChatModelRoute(providerId, modelId));

    private static async Task<SessionTurnRecord> WaitForPhaseAsync(
        ISessionStore store,
        Guid turnId,
        SessionTurnPhase phase)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var turn = await store.GetTurnAsync(turnId);
            if (turn?.Phase == phase)
            {
                return turn;
            }

            if (turn is not null && SessionTurnPhases.IsTerminal(turn.Phase))
            {
                throw new InvalidOperationException(
                    $"Turn 在到达 {phase} 前进入终态 {turn.Phase}：{turn.FailureCode} {turn.FailureMessage}");
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Turn 未在预期时间内进入 {phase}。");
    }

    private sealed class MutableSettingsStore(AiSettings current) : IAiSettingsStore
    {
        private AiSettings _current = current;

        public int LoadCount { get; private set; }

        public Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            return Task.FromResult(_current);
        }

        public Task SaveAsync(
            AiSettings settings,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingSettingsStore(AiSettings settings) : IAiSettingsStore
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LoadCount { get; private set; }

        public async Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return settings;
        }

        public Task SaveAsync(
            AiSettings value,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class MetadataOnlyProvider(string providerId, string modelId) : IChatModelProvider
    {
        public ChatProviderDescriptor Descriptor { get; } = new(
            providerId,
            providerId,
            $"{providerId} isolated destination",
            SendsDataOffDevice: false,
            [new ChatModelDescriptor(modelId, modelId, ChatModelCapabilities.None)]);

        public int CompleteCount { get; private set; }

        public Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            CompleteCount++;
            throw new InvalidOperationException("冻结路由解析不得执行 Provider。");
        }

        public Task<ChatProviderHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("冻结路由解析不得执行健康检查。");

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
