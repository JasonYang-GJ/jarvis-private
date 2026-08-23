using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class AssistantCommandServiceTests
{
    [Fact]
    public async Task ApplicationPlanRequiresVisibleConfirmationAndRunsOnlyOnce()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var launcher = new RecordingLauncher();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IDesktopProcessLauncher>(launcher));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var rejectedPlan = await client.PlanAssistantCommandAsync(
            new PlanAssistantCommandRequestDto("打开记事本"));
        var rejected = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ExecuteAssistantCommandAsync(
                new ExecuteAssistantCommandRequestDto(rejectedPlan.PlanId, false)));
        var plan = await client.PlanAssistantCommandAsync(
            new PlanAssistantCommandRequestDto("打开记事本"));
        var completed = await client.ExecuteAssistantCommandAsync(
            new ExecuteAssistantCommandRequestDto(plan.PlanId, true, "assistant-open-notepad"));
        var repeated = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ExecuteAssistantCommandAsync(
                new ExecuteAssistantCommandRequestDto(plan.PlanId, true, "assistant-open-notepad")));
        await host.StopAsync();

        Assert.Equal("Ready", plan.Readiness);
        Assert.Equal("OpenApplication", plan.IntentKind);
        Assert.Equal("notepad", plan.CanonicalTarget);
        Assert.True(plan.RequiresConfirmation);
        Assert.Equal("desktop_action_not_authorized", rejected.Error.Code);
        Assert.Equal("Completed", completed.Status);
        Assert.Equal("ExecutionVerified", completed.VerificationStatus);
        Assert.NotNull(completed.Evidence);
        Assert.Contains(completed.Evidence.UnverifiedFacts, fact =>
            fact.Contains("完全加载", StringComparison.Ordinal));
        Assert.Equal("action_plan_expired", repeated.Error.Code);
        Assert.Equal(["notepad.exe"], launcher.Targets);
    }

    [Fact]
    public async Task OrdinaryQuestionPlansConversationWithoutComputerAuthorization()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var plan = await client.PlanAssistantCommandAsync(
            new PlanAssistantCommandRequestDto("请解释一下什么是文件夹"));
        await host.StopAsync();

        Assert.Equal("Conversation", plan.IntentKind);
        Assert.Equal("Ready", plan.Readiness);
        Assert.False(plan.RequiresConfirmation);
        Assert.Contains("不操作电脑", plan.UserSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompoundChromeDouyinCommandUsesRequestedBrowserAndVerifiedVisibleLaunch()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var launcher = new RecordingLauncher();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IDesktopProcessLauncher>(launcher);
            services.AddSingleton<IInstalledApplicationCatalog>(new BrowserCatalog());
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var plan = await client.PlanAssistantCommandAsync(
            new PlanAssistantCommandRequestDto("打开谷歌浏览器界面并搜索打开抖音"));
        var result = await client.ExecuteAssistantCommandAsync(
            new ExecuteAssistantCommandRequestDto(plan.PlanId, true, "visible-chrome-douyin"));
        await host.StopAsync();

        Assert.Contains("Google Chrome", plan.UserSummary, StringComparison.Ordinal);
        Assert.Equal("https://www.douyin.com/", plan.CanonicalTarget);
        Assert.Equal("ExecutionVerified", result.VerificationStatus);
        Assert.Equal("chrome.exe", launcher.VisibleBrowserTarget);
        Assert.Equal("https://www.douyin.com/", launcher.VisibleWebsite?.AbsoluteUri);
        Assert.Contains(result.Evidence!.VerifiedFacts,
            fact => fact.Contains("前台", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExplicitLowRiskVoiceCommandExecutesWithoutClickConfirmation()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var launcher = new RecordingLauncher();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IDesktopProcessLauncher>(launcher);
            services.AddSingleton<IInstalledApplicationCatalog>(new BrowserCatalog());
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var plan = await client.PlanAssistantCommandAsync(
            new PlanAssistantCommandRequestDto(
                "打开谷歌浏览器",
                InputModality: "Voice"));
        var result = await client.ExecuteAssistantCommandAsync(
            new ExecuteAssistantCommandRequestDto(
                plan.PlanId,
                Confirmed: false,
                IdempotencyKey: "explicit-voice-open-chrome",
                AuthorizationSource: "ExplicitVoice"));
        await host.StopAsync();

        Assert.Equal("Completed", result.Status);
        Assert.Equal("ExecutionVerified", result.VerificationStatus);
        Assert.Equal("chrome.exe", launcher.VisibleApplicationTarget);
    }

    [Fact]
    public async Task VoiceAuthorizationDoesNotBypassFileOrHighRiskBoundaries()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var path = Path.Combine(Path.GetTempPath(), $"voice-boundary-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "fake test data");
        try
        {
            var plan = await client.PlanAssistantCommandAsync(
                new PlanAssistantCommandRequestDto(
                    "打开这个文件",
                    SelectedFilePath: path,
                    InputModality: "Voice"));

            var exception = await Assert.ThrowsAsync<DesktopApiException>(() =>
                client.ExecuteAssistantCommandAsync(
                    new ExecuteAssistantCommandRequestDto(
                        plan.PlanId,
                        Confirmed: false,
                        AuthorizationSource: "ExplicitVoice")));

            Assert.Equal("desktop_action_not_authorized", exception.Error.Code);
        }
        finally
        {
            File.Delete(path);
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task MissingSelectedFileIsRejectedBeforePlanning()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var exception = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.PlanAssistantCommandAsync(new PlanAssistantCommandRequestDto(
                "打开这个文件",
                Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.txt"))));
        await host.StopAsync();

        Assert.Equal("file_missing", exception.Error.Code);
    }

    [Fact]
    public async Task StopWindowObservationCancelsHostWorkAndClearsFrame()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var captureBytes = new byte[] { 4, 3, 2, 1 };
        var vision = new BlockingVisionProvider();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IForegroundWindowContextProvider>(
                new FixedForegroundProvider());
            services.AddSingleton<IWindowCaptureService>(
                new FixedCaptureService(captureBytes));
            services.AddSingleton<IWindowVisionProvider>(vision);
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        const string operationId = "window-observation-cancel-test";
        var plan = await client.PlanAssistantCommandAsync(
            new PlanAssistantCommandRequestDto(
                "我现在打开的是什么界面",
                ForegroundObservationConsent: true));

        var execution = client.ExecuteAssistantCommandAsync(
            new ExecuteAssistantCommandRequestDto(plan.PlanId, true, operationId));
        await vision.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cancellationAccepted = await client.CancelWindowObservationAsync(operationId);
        await Assert.ThrowsAsync<DesktopApiException>(() => execution);
        await host.StopAsync();

        Assert.True(cancellationAccepted);
        Assert.True(vision.WasCancelled);
        Assert.All(captureBytes, value => Assert.Equal(0, value));
    }

    private sealed class RecordingLauncher : IDesktopProcessLauncher
    {
        public List<string> Targets { get; } = [];

        public string? VisibleBrowserTarget { get; private set; }

        public Uri? VisibleWebsite { get; private set; }

        public string? VisibleApplicationTarget { get; private set; }

        public int? Start(string target)
        {
            Targets.Add(target);
            return 99;
        }

        public VisibleDesktopLaunchResult OpenApplicationVisible(string applicationLaunchTarget)
        {
            VisibleApplicationTarget = applicationLaunchTarget;
            Targets.Add(applicationLaunchTarget);
            return new VisibleDesktopLaunchResult(99, 76, "测试应用");
        }

        public VisibleDesktopLaunchResult OpenWebsiteVisible(
            string? browserLaunchTarget,
            Uri website)
        {
            VisibleBrowserTarget = browserLaunchTarget;
            VisibleWebsite = website;
            Targets.Add(website.AbsoluteUri);
            return new VisibleDesktopLaunchResult(99, 77, "测试浏览器");
        }
    }

    private sealed class BrowserCatalog : IInstalledApplicationCatalog
    {
        private static readonly KnownDesktopApplication Chrome =
            new("chrome-app", "Google Chrome", "chrome.exe");

        public IReadOnlyList<KnownDesktopApplication> GetApplications() => [Chrome];

        public KnownDesktopApplication? FindById(string id) =>
            id == Chrome.Id ? Chrome : null;

        public KnownDesktopApplication? FindByDisplayName(string displayName) =>
            string.Equals(displayName, Chrome.DisplayName, StringComparison.OrdinalIgnoreCase)
                ? Chrome
                : null;

        public KnownDesktopApplication? FindBrowser(string browserName) =>
            browserName.Contains("Chrome", StringComparison.OrdinalIgnoreCase)
            || browserName.Contains("谷歌", StringComparison.Ordinal)
                ? Chrome
                : null;
    }

    private sealed class FixedForegroundProvider : IForegroundWindowContextProvider
    {
        public ForegroundWindowSnapshot GetLastExternalWindow() =>
            new(72, "自动测试窗口", "testhost", DateTimeOffset.UtcNow);
    }

    private sealed class FixedCaptureService(byte[] bytes) : IWindowCaptureService
    {
        public Task<CapturedWindowFrame> CaptureAsync(
            WindowCaptureTarget target,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CapturedWindowFrame(bytes, 100, 60, "mock"));
    }

    private sealed class BlockingVisionProvider : IWindowVisionProvider
    {
        public string ProviderId => "blocking-test";
        public bool SendsImageOffDevice => false;
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasCancelled { get; private set; }

        public async Task<WindowVisionResult> AnalyzeAsync(
            WindowVisionRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }

            throw new InvalidOperationException("Unreachable");
        }
    }
}
