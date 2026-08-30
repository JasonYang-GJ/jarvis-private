using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.Core.Conversations;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class SessionCoordinatorAdversarialTests
{
    [Fact]
    public async Task RetryingTheSameSessionInputDoesNotCancelOrStartASecondProviderTurn()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new ControlledConversationProvider();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IConversationProvider>(provider));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("幂等重试");
        const string idempotencyKey = "same-session-input";

        var first = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "请生成一个需要等待的回答",
            "Text",
            idempotencyKey,
            session.SessionId));
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var duplicate = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "请生成一个需要等待的回答",
            "Text",
            idempotencyKey,
            session.SessionId));
        var whileRunning = await client.GetCurrentSessionAsync();

        Assert.True(duplicate.WasDuplicate);
        Assert.Equal(first.TurnId, duplicate.TurnId);
        Assert.Equal(1, provider.SendCount);
        Assert.Equal(0, provider.CancelCount);
        Assert.False(provider.CancellationObserved);
        Assert.Equal("Responding", whileRunning?.Turns.Single().Phase);

        provider.Complete("原请求正常完成");
        var completed = await WaitForTurnPhaseAsync(client, first.TurnId, "Completed");
        await host.StopAsync();

        Assert.Single(completed.Turns);
        Assert.Equal(2, completed.Messages.Count);
        Assert.Equal("原请求正常完成", completed.Messages[^1].Content);
    }

    [Fact]
    public async Task AnotherSessionCannotProvideAProjectForTheOriginalTurn()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var (_, project, _) = await environment.SeedProjectsAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var otherSession = await client.StartNewSessionAsync("另一个会话");
        var originalSession = await client.StartNewSessionAsync("原会话");
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "帮我修改一下这个项目并运行测试",
            "Text",
            "cross-session-project",
            originalSession.SessionId));
        await WaitForTurnPhaseAsync(client, submitted.TurnId, "WaitingForProject");
        var rejected = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ProvideSessionProjectAsync(
                otherSession.SessionId,
                submitted.TurnId,
                project.Id));
        var original = await client.SetCurrentSessionAsync(originalSession.SessionId);
        await host.StopAsync();

        Assert.Equal("project_not_authorized", rejected.Error.Code);
        Assert.Null(original.SelectedProjectId);
        Assert.Equal(
            "WaitingForProject",
            original.Turns.Single(turn => turn.Id == submitted.TurnId).Phase);
    }

    [Fact]
    public async Task ConcurrentFileConfirmationsLaunchTheSelectedFileAtMostOnce()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var launcher = new CountingDesktopLauncher();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IDesktopProcessLauncher>(launcher));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("文件双确认");
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "帮我打开这个文件",
            "Text",
            "double-file-confirm",
            session.SessionId));
        await WaitForTurnPhaseAsync(client, submitted.TurnId, "WaitingForFile");
        var file = Path.Combine(environment.RootDirectory, "double-confirm.txt");
        await File.WriteAllTextAsync(file, "synthetic test file");
        await client.ProvideSessionFileAsync(session.SessionId, submitted.TurnId, file);

        var confirmations = await Task.WhenAll(
            CaptureAsync(() => client.ConfirmSessionTurnAsync(
                session.SessionId,
                submitted.TurnId,
                confirmed: true)),
            CaptureAsync(() => client.ConfirmSessionTurnAsync(
                session.SessionId,
                submitted.TurnId,
                confirmed: true)));
        var final = await WaitForTurnPhaseAsync(client, submitted.TurnId, "Completed");
        await host.StopAsync();

        Assert.Contains(confirmations, result => result.Snapshot is not null);
        Assert.Equal(1, launcher.StartCount);
        Assert.Equal(Path.GetFullPath(file), launcher.LastTarget);
        Assert.Equal("Completed", final.Turns.Single(turn => turn.Id == submitted.TurnId).Phase);
    }

    [Fact]
    public async Task ConcurrentProjectConfirmationsCreateAtMostOneProgrammingTask()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var (_, project, _) = await environment.SeedProjectsAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("项目双确认");
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "帮我修改这个项目并运行测试 TEST_DELAYED_SUCCESS",
            "Text",
            "double-project-confirm",
            session.SessionId));
        await WaitForTurnPhaseAsync(client, submitted.TurnId, "WaitingForProject");
        await client.ProvideSessionProjectAsync(session.SessionId, submitted.TurnId, project.Id);

        var confirmations = await Task.WhenAll(
            CaptureAsync(() => client.ConfirmSessionTurnAsync(
                session.SessionId,
                submitted.TurnId,
                confirmed: true)),
            CaptureAsync(() => client.ConfirmSessionTurnAsync(
                session.SessionId,
                submitted.TurnId,
                confirmed: true)));
        var tasks = await client.ListTasksAsync(project.Id);
        var snapshot = await client.GetCurrentSessionAsync();
        await host.StopAsync();

        Assert.Contains(confirmations, result => result.Snapshot is not null);
        Assert.Single(tasks);
        Assert.Equal(
            tasks[0].Id,
            snapshot?.Turns.Single(turn => turn.Id == submitted.TurnId).TaskId);
    }

    [Fact]
    public async Task ProviderFailureEndsTheTurnAndTheSameSessionCanContinue()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new FailOnceConversationProvider();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IConversationProvider>(provider));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("Provider 失败恢复");

        var first = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "第一次请求应失败",
            "Text",
            "provider-failure-first",
            session.SessionId));
        var failed = await WaitForTurnPhaseAsync(client, first.TurnId, "Failed");

        Assert.Empty(failed.ActiveTurns);
        Assert.NotNull(failed.Turns.Single(turn => turn.Id == first.TurnId).FailureMessage);

        var second = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "失败后继续",
            "Text",
            "provider-failure-second",
            session.SessionId));
        var recovered = await WaitForTurnPhaseAsync(client, second.TurnId, "Completed");
        await host.StopAsync();

        Assert.Equal(session.SessionId, recovered.SessionId);
        Assert.Equal(["Failed", "Completed"], recovered.Turns.Select(turn => turn.Phase).ToArray());
        Assert.Equal("恢复后的回答", recovered.Messages[^1].Content);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal("thread-provider-recovery", provider.Requests[1].ExternalThreadId);
    }

    [Fact]
    public async Task SessionUpdateWaitReturnsANewerVersionAndOtherwiseWaitsForTimeout()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new ControlledConversationProvider();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IConversationProvider>(provider));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("增量更新");

        var updateWait = client.WaitForSessionUpdateAsync(
            session.ChangeVersion,
            waitMilliseconds: 2_000);
        await Task.Delay(50);
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "触发一次会话状态变化",
            "Text",
            "session-update-change",
            session.SessionId));
        var changed = await updateWait.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.NotNull(changed);
        Assert.True(changed.ChangeVersion > session.ChangeVersion);
        Assert.Contains(changed.Turns, turn => turn.Id == submitted.TurnId);

        provider.Complete("增量更新完成");
        await WaitForTurnPhaseAsync(client, submitted.TurnId, "Completed");
        await Task.Delay(100);
        var stable = await client.GetCurrentSessionAsync();
        Assert.NotNull(stable);
        var stopwatch = Stopwatch.StartNew();
        var timedOut = await client.WaitForSessionUpdateAsync(
            stable.ChangeVersion,
            waitMilliseconds: 150);
        stopwatch.Stop();
        await host.StopAsync();

        Assert.NotNull(timedOut);
        Assert.Equal(stable.ChangeVersion, timedOut.ChangeVersion);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task SessionUpdateWaitDoesNotSpinWhenNoSessionExistsYet()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var updateWait = client.WaitForSessionUpdateAsync(
            knownChangeVersion: -1,
            waitMilliseconds: 2_000);
        await Task.Delay(150);
        Assert.False(updateWait.IsCompleted);

        var created = await client.StartNewSessionAsync("首次话题");
        var changed = await updateWait.WaitAsync(TimeSpan.FromSeconds(3));
        await host.StopAsync();

        Assert.NotNull(changed);
        Assert.Equal(created.SessionId, changed.SessionId);
        Assert.True(changed.ChangeVersion >= created.ChangeVersion);
    }

    [Fact]
    public async Task HostRestartInterruptsRunningWorkWithoutReplayingIt()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new RestartBlockingConversationProvider();
        Guid runningSessionId;
        Guid runningTurnId;

        var firstHost = environment.BuildHost(services =>
            services.AddSingleton<IConversationProvider>(provider));
        await firstHost.StartAsync();
        IDesktopApiClient firstClient = new DesktopApiClient(environment.Options.PipeName);
        var runningSession = await firstClient.StartNewSessionAsync("重启运行态");
        runningSessionId = runningSession.SessionId;
        var runningTurn = await firstClient.SubmitSessionInputAsync(new SessionInputRequestDto(
            "保持回答直到 Host 中断",
            "Text",
            "restart-running-turn",
            runningSession.SessionId));
        runningTurnId = runningTurn.TurnId;
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitForTurnPhaseAsync(firstClient, runningTurnId, "Responding");

        firstHost.Dispose();

        using var secondHost = environment.BuildHost(services =>
            services.AddSingleton<IConversationProvider>(new FailOnceConversationProvider()));
        await secondHost.StartAsync();
        IDesktopApiClient secondClient = new DesktopApiClient(environment.Options.PipeName);
        var recoveredRunning = await secondClient.SetCurrentSessionAsync(runningSessionId);

        Assert.Equal(
            "Interrupted",
            recoveredRunning.Turns.Single(turn => turn.Id == runningTurnId).Phase);

        provider.ReleaseLateReply();
        await Task.Delay(100);
        var stillWaiting = await secondClient.GetCurrentSessionAsync();
        await secondHost.StopAsync();

        Assert.Equal(
            "Interrupted",
            stillWaiting?.Turns.Single(turn => turn.Id == runningTurnId).Phase);
        Assert.DoesNotContain(
            stillWaiting?.Messages ?? [],
            message => message.Content.Contains("迟到回答", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostRestartKeepsAProjectRequestWaitingWithoutRequiringTheUserToRepeatIt()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        Guid sessionId;
        Guid turnId;

        var firstHost = environment.BuildHost();
        await firstHost.StartAsync();
        IDesktopApiClient firstClient = new DesktopApiClient(environment.Options.PipeName);
        var session = await firstClient.StartNewSessionAsync("重启等待项目");
        sessionId = session.SessionId;
        var submitted = await firstClient.SubmitSessionInputAsync(new SessionInputRequestDto(
            "帮我修改一下这个项目",
            "Text",
            "restart-waiting-project",
            session.SessionId));
        turnId = submitted.TurnId;
        await WaitForTurnPhaseAsync(firstClient, turnId, "WaitingForProject");
        firstHost.Dispose();

        using var secondHost = environment.BuildHost();
        await secondHost.StartAsync();
        IDesktopApiClient secondClient = new DesktopApiClient(environment.Options.PipeName);
        var recovered = await secondClient.SetCurrentSessionAsync(sessionId);
        await secondHost.StopAsync();

        var turn = recovered.Turns.Single(item => item.Id == turnId);
        Assert.Equal("WaitingForProject", turn.Phase);
        Assert.Equal("帮我修改一下这个项目", turn.InputText);
        Assert.Empty(recovered.Messages);
    }

    [Fact]
    public async Task WindowChangingAfterConsentRequiresNewConsentWithoutCapturingAnything()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var foreground = new MutableForegroundProvider(
            new ForegroundWindowSnapshot(
                8501,
                "窗口 A",
                "stage1-a",
                85010,
                new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero),
                DateTimeOffset.UtcNow));
        var capture = new CountingCaptureService();
        var vision = new CountingVisionProvider();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IForegroundWindowContextProvider>(foreground);
            services.AddSingleton<IWindowCaptureService>(capture);
            services.AddSingleton<IWindowVisionProvider>(vision);
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("窗口换目标");
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "看看这个窗口是什么",
            "Text",
            "window-target-changed",
            session.SessionId));
        var waitingForA = await WaitForTurnPhaseAsync(
            client,
            submitted.TurnId,
            "WaitingForWindowConsent");
        Assert.Equal(
            8501,
            waitingForA.Turns.Single(turn => turn.Id == submitted.TurnId).WindowHandle);

        foreground.Current = new ForegroundWindowSnapshot(
            8502,
            "窗口 B",
            "stage1-b",
            85020,
            new DateTimeOffset(2026, 8, 30, 1, 1, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow);
        var retargeted = await client.RespondSessionWindowConsentAsync(
            session.SessionId,
            submitted.TurnId,
            granted: true);
        await host.StopAsync();

        var turn = retargeted.Turns.Single(item => item.Id == submitted.TurnId);
        Assert.Equal("WaitingForWindowConsent", turn.Phase);
        Assert.Equal(8502, turn.WindowHandle);
        Assert.Equal("窗口 B", turn.WindowTitle);
        Assert.Equal(0, capture.CallCount);
        Assert.Equal(0, vision.CallCount);
    }

    [Fact]
    public async Task AnotherSessionCannotGrantWindowConsentForTheOriginalTurn()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var foreground = new MutableForegroundProvider(
            new ForegroundWindowSnapshot(
                8601,
                "授权测试窗口",
                "stage1-consent",
                86010,
                new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero),
                DateTimeOffset.UtcNow));
        var capture = new CountingCaptureService();
        var vision = new CountingVisionProvider();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IForegroundWindowContextProvider>(foreground);
            services.AddSingleton<IWindowCaptureService>(capture);
            services.AddSingleton<IWindowVisionProvider>(vision);
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var otherSession = await client.StartNewSessionAsync("其他窗口会话");
        var originalSession = await client.StartNewSessionAsync("原窗口会话");
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "看看这个窗口是什么",
            "Text",
            "cross-session-window-consent",
            originalSession.SessionId));
        await WaitForTurnPhaseAsync(client, submitted.TurnId, "WaitingForWindowConsent");
        var rejected = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.RespondSessionWindowConsentAsync(
                otherSession.SessionId,
                submitted.TurnId,
                granted: true));
        var original = await client.SetCurrentSessionAsync(originalSession.SessionId);
        await host.StopAsync();

        Assert.Equal("project_not_authorized", rejected.Error.Code);
        Assert.Equal(
            "WaitingForWindowConsent",
            original.Turns.Single(turn => turn.Id == submitted.TurnId).Phase);
        Assert.Equal(0, capture.CallCount);
        Assert.Equal(0, vision.CallCount);
    }

    private static async Task<CallOutcome> CaptureAsync(Func<Task<SessionSnapshotDto>> call)
    {
        try
        {
            return new CallOutcome(await call(), null);
        }
        catch (DesktopApiException exception)
        {
            return new CallOutcome(null, exception);
        }
    }

    private static async Task<SessionSnapshotDto> WaitForTurnPhaseAsync(
        IDesktopApiClient client,
        Guid turnId,
        string expectedPhase,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await client.GetCurrentSessionAsync();
            if (snapshot?.Turns.SingleOrDefault(turn => turn.Id == turnId)?.Phase == expectedPhase)
            {
                return snapshot;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"统一会话请求没有进入 {expectedPhase}。");
    }

    private sealed class ControlledConversationProvider : IConversationProvider
    {
        private readonly TaskCompletionSource<string> _reply =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId => "session-adversarial-controlled";

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SendCount { get; private set; }

        public int CancelCount { get; private set; }

        public bool CancellationObserved { get; private set; }

        public async Task<ConversationProviderResult> SendAsync(
            ConversationProviderRequest request,
            Func<string, int, Task>? started = null,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            if (started is not null)
            {
                await started("thread-idempotency", 8101);
            }

            Started.TrySetResult();
            using var registration = cancellationToken.Register(() => CancellationObserved = true);
            try
            {
                var reply = await _reply.Task.WaitAsync(cancellationToken);
                return new ConversationProviderResult(
                    ConversationProviderOutcome.Succeeded,
                    "thread-idempotency",
                    reply,
                    "message-idempotency",
                    8101);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                return new ConversationProviderResult(
                    ConversationProviderOutcome.Cancelled,
                    "thread-idempotency",
                    null,
                    null,
                    8101,
                    "cancelled",
                    "回答已停止。");
            }
        }

        public Task CancelAsync(Guid conversationId, CancellationToken cancellationToken = default)
        {
            CancelCount++;
            return Task.CompletedTask;
        }

        public void Complete(string reply) => _reply.TrySetResult(reply);

        public ValueTask DisposeAsync()
        {
            _reply.TrySetCanceled();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingDesktopLauncher : IDesktopProcessLauncher
    {
        private int _startCount;

        public int StartCount => Volatile.Read(ref _startCount);

        public string? LastTarget { get; private set; }

        public int? Start(string target)
        {
            LastTarget = target;
            Interlocked.Increment(ref _startCount);
            return 8201;
        }

        public VisibleDesktopLaunchResult OpenApplicationVisible(string applicationLaunchTarget) =>
            new(8201, 8202, "合成测试应用");

        public VisibleDesktopLaunchResult OpenWebsiteVisible(string? browserLaunchTarget, Uri website) =>
            new(8201, 8203, "合成测试浏览器");
    }

    private sealed class FailOnceConversationProvider : IConversationProvider
    {
        public string ProviderId => "session-adversarial-fail-once";

        public List<ConversationProviderRequest> Requests { get; } = [];

        public async Task<ConversationProviderResult> SendAsync(
            ConversationProviderRequest request,
            Func<string, int, Task>? started = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (started is not null)
            {
                await started("thread-provider-recovery", 8300 + Requests.Count);
            }

            return Requests.Count == 1
                ? new ConversationProviderResult(
                    ConversationProviderOutcome.Failed,
                    "thread-provider-recovery",
                    null,
                    null,
                    8301,
                    "synthetic_provider_failure",
                    "合成 Provider 失败。")
                : new ConversationProviderResult(
                    ConversationProviderOutcome.Succeeded,
                    "thread-provider-recovery",
                    "恢复后的回答",
                    "message-provider-recovery",
                    8302);
        }

        public Task CancelAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RestartBlockingConversationProvider : IConversationProvider
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId => "session-adversarial-restart";

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ConversationProviderResult> SendAsync(
            ConversationProviderRequest request,
            Func<string, int, Task>? started = null,
            CancellationToken cancellationToken = default)
        {
            if (started is not null)
            {
                await started("thread-restart-interruption", 8401);
            }

            Started.TrySetResult();
            await _release.Task;
            return new ConversationProviderResult(
                ConversationProviderOutcome.Succeeded,
                "thread-restart-interruption",
                "重启后不应出现的迟到回答",
                "message-restart-late",
                8401);
        }

        public Task CancelAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void ReleaseLateReply() => _release.TrySetResult();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MutableForegroundProvider(ForegroundWindowSnapshot current)
        : IForegroundWindowContextProvider
    {
        public ForegroundWindowSnapshot Current { get; set; } = current;

        public ForegroundWindowSnapshot GetLastExternalWindow() => Current;
    }

    private sealed class CountingCaptureService : IWindowCaptureService
    {
        public int CallCount { get; private set; }

        public Task<CapturedWindowFrame> CaptureAsync(
            WindowCaptureTarget target,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new CapturedWindowFrame([1, 2, 3], 10, 10, "synthetic"));
        }
    }

    private sealed class CountingVisionProvider : IWindowVisionProvider
    {
        public string ProviderId => "session-adversarial-window";

        public bool SendsImageOffDevice => false;

        public int CallCount { get; private set; }

        public Task<WindowVisionResult> AnalyzeAsync(
            WindowVisionRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new WindowVisionResult(
                "不应执行的窗口分析",
                "Synthetic",
                ["不应执行"],
                ["合成测试"]));
        }
    }

    private sealed record CallOutcome(SessionSnapshotDto? Snapshot, DesktopApiException? Error);
}
