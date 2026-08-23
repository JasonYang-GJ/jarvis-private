using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class SessionContextContinuationTests
{
    [Fact]
    public async Task RevokedSelectedProjectReturnsTheOriginalRequestToWaitingForProject()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var (_, project, _) = await environment.SeedProjectsAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("项目撤权后续接");
        var first = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "先建立项目上下文",
            "ProgrammingTask",
            "select-project-before-revoke",
            session.SessionId));
        await WaitForPhaseAsync(client, first.TurnId, "WaitingForProject");
        await client.ProvideSessionProjectAsync(session.SessionId, first.TurnId, project.Id);
        await client.ConfirmSessionTurnAsync(session.SessionId, first.TurnId, confirmed: false);
        await client.RevokeProjectAsync(project.Id);

        const string originalRequest = "继续修改这个项目，但不要丢掉这句话";
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            originalRequest,
            "ProgrammingTask",
            "revoked-project-must-wait",
            session.SessionId));
        var waiting = await WaitForPhaseAsync(client, submitted.TurnId, "WaitingForProject");
        await host.StopAsync();

        var turn = waiting.Turns.Single(item => item.Id == submitted.TurnId);
        Assert.Equal(originalRequest, turn.InputText);
        Assert.Equal("WaitingForProject", turn.Phase);
        Assert.Equal("Project", turn.MissingContext);
        Assert.Null(waiting.SelectedProjectId);
    }

    [Fact]
    public async Task ExplicitProgrammingPageUsesTheSameSessionTurnEvenForAmbiguousText()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var (_, project, _) = await environment.SeedProjectsAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("显式编程任务");
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "把刚才那个问题处理好",
            "ProgrammingTask",
            "explicit-programming-surface",
            session.SessionId));

        await WaitForPhaseAsync(client, submitted.TurnId, "WaitingForProject");
        var selected = await client.ProvideSessionProjectAsync(
            session.SessionId,
            submitted.TurnId,
            project.Id);
        await host.StopAsync();

        var turn = selected.Turns.Single(item => item.Id == submitted.TurnId);
        Assert.Equal("把刚才那个问题处理好", turn.InputText);
        Assert.Equal("CodingTask", turn.WorkKind);
        Assert.Equal("WaitingForConfirmation", turn.Phase);
        Assert.Equal(project.Id, turn.ProjectId);
    }

    [Fact]
    public async Task ProjectSelectionResumesTheOriginalCodingRequestWithoutRepeatingIt()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var (_, project, _) = await environment.SeedProjectsAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("项目补充");
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "帮我修改一下这个项目并运行测试",
            "Text",
            "project-context",
            session.SessionId));
        var waiting = await WaitForPhaseAsync(client, submitted.TurnId, "WaitingForProject");

        var selected = await client.ProvideSessionProjectAsync(
            session.SessionId,
            submitted.TurnId,
            project.Id);
        var awaitingConfirmation = selected.Turns.Single(turn => turn.Id == submitted.TurnId);
        Assert.Equal("WaitingForConfirmation", awaitingConfirmation.Phase);
        Assert.Equal("帮我修改一下这个项目并运行测试", awaitingConfirmation.InputText);
        Assert.Equal(project.Id, selected.SelectedProjectId);

        var confirmed = await client.ConfirmSessionTurnAsync(
            session.SessionId,
            submitted.TurnId,
            confirmed: true);
        var resumed = confirmed.Turns.Single(turn => turn.Id == submitted.TurnId);
        await host.StopAsync();

        Assert.Equal(project.Id, resumed.ProjectId);
        Assert.NotNull(resumed.TaskId);
        Assert.Contains(resumed.Phase, new[] { "ProgrammingTask", "WaitingForUser", "Completed", "Failed" });
    }

    [Fact]
    public async Task FileSelectionResumesTheOriginalRequestAndStillRequiresVisibleConfirmation()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var launcher = new FileRecordingLauncher();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IDesktopProcessLauncher>(launcher));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("文件补充");
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "帮我打开这个文件",
            "Text",
            "file-context",
            session.SessionId));
        await WaitForPhaseAsync(client, submitted.TurnId, "WaitingForFile");
        var file = Path.Combine(environment.RootDirectory, "selected-file.txt");
        await File.WriteAllTextAsync(file, "fake stage1 content");

        var selected = await client.ProvideSessionFileAsync(
            session.SessionId,
            submitted.TurnId,
            file);
        var awaitingConfirmation = selected.Turns.Single(turn => turn.Id == submitted.TurnId);
        Assert.Equal("WaitingForConfirmation", awaitingConfirmation.Phase);
        Assert.Equal(Path.GetFullPath(file), awaitingConfirmation.FilePath);
        Assert.Empty(launcher.Targets);

        var confirmed = await client.ConfirmSessionTurnAsync(
            session.SessionId,
            submitted.TurnId,
            confirmed: true);
        await host.StopAsync();

        Assert.Equal("Completed", confirmed.Turns.Single(turn => turn.Id == submitted.TurnId).Phase);
        Assert.Equal([Path.GetFullPath(file)], launcher.Targets);
    }

    [Fact]
    public async Task WindowConsentApproveAndRejectBothFinishTheSameOriginalTurnSafely()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var captureBytes = new byte[] { 9, 8, 7, 6 };
        var vision = new RecordingVisionProvider();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IForegroundWindowContextProvider>(new FixedForegroundProvider());
            services.AddSingleton<IWindowCaptureService>(new FixedCaptureService(captureBytes));
            services.AddSingleton<IWindowVisionProvider>(vision);
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("窗口授权");

        var rejectedInput = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "看看这个窗口是什么",
            "Text",
            "window-reject",
            session.SessionId));
        var waitingReject = await WaitForPhaseAsync(client, rejectedInput.TurnId, "WaitingForWindowConsent");
        Assert.Equal("阶段一测试窗口", waitingReject.Turns.Single(turn => turn.Id == rejectedInput.TurnId).WindowTitle);
        var rejected = await client.RespondSessionWindowConsentAsync(
            session.SessionId,
            rejectedInput.TurnId,
            granted: false);
        Assert.Equal("Cancelled", rejected.Turns.Single(turn => turn.Id == rejectedInput.TurnId).Phase);
        Assert.Equal(0, vision.CallCount);

        var approvedInput = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "看看这个窗口是什么",
            "Text",
            "window-approve",
            session.SessionId));
        await WaitForPhaseAsync(client, approvedInput.TurnId, "WaitingForWindowConsent");
        var approved = await client.RespondSessionWindowConsentAsync(
            session.SessionId,
            approvedInput.TurnId,
            granted: true);
        await host.StopAsync();

        Assert.Equal("Completed", approved.Turns.Single(turn => turn.Id == approvedInput.TurnId).Phase);
        Assert.Equal(1, vision.CallCount);
        Assert.All(captureBytes, value => Assert.Equal(0, value));
    }

    private static async Task<SessionSnapshotDto> WaitForPhaseAsync(
        IDesktopApiClient client,
        Guid turnId,
        string phase)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await client.GetCurrentSessionAsync();
            if (snapshot?.Turns.SingleOrDefault(turn => turn.Id == turnId)?.Phase == phase)
            {
                return snapshot;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"统一会话没有进入 {phase}。");
    }

    private sealed class FileRecordingLauncher : IDesktopProcessLauncher
    {
        public List<string> Targets { get; } = [];

        public int? Start(string target)
        {
            Targets.Add(target);
            return 901;
        }

        public VisibleDesktopLaunchResult OpenApplicationVisible(string applicationLaunchTarget) =>
            new(901, 902, "测试应用");

        public VisibleDesktopLaunchResult OpenWebsiteVisible(string? browserLaunchTarget, Uri website) =>
            new(901, 903, "测试浏览器");
    }

    private sealed class FixedForegroundProvider : IForegroundWindowContextProvider
    {
        public ForegroundWindowSnapshot GetLastExternalWindow() =>
            new(812, "阶段一测试窗口", "stage1-window", DateTimeOffset.UtcNow);
    }

    private sealed class FixedCaptureService(byte[] bytes) : IWindowCaptureService
    {
        public Task<CapturedWindowFrame> CaptureAsync(
            WindowCaptureTarget target,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CapturedWindowFrame(bytes, 80, 50, "stage1-test"));
    }

    private sealed class RecordingVisionProvider : IWindowVisionProvider
    {
        public string ProviderId => "stage1-window-test";

        public bool SendsImageOffDevice => false;

        public int CallCount { get; private set; }

        public Task<WindowVisionResult> AnalyzeAsync(
            WindowVisionRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new WindowVisionResult(
                "这是阶段一测试窗口。",
                "Synthetic",
                ["合成测试结果"],
                ["没有真实用户数据"]));
        }
    }
}
