using System.Text.Json;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class WindowsDesktopSkillAdapterTests
{
    [Fact]
    public async Task OnlyCatalogApplicationIdCanLaunch()
    {
        var launcher = new RecordingLauncher();
        var adapter = CreateAdapter(launcher);

        var result = await adapter.StartAsync(Request(
            WindowsDesktopCapabilities.OpenApplication,
            new WindowsDesktopActionInput("OpenApplication", "test-app")));
        var denied = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.StartAsync(Request(
                WindowsDesktopCapabilities.OpenApplication,
                new WindowsDesktopActionInput("OpenApplication", "unknown"))));

        Assert.Equal(SkillExecutionStatus.Succeeded, result.Status);
        Assert.Equal(["test.exe"], launcher.Targets);
        Assert.Contains("清单", denied.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpWebsiteNeverLaunches()
    {
        var launcher = new RecordingLauncher();
        var adapter = CreateAdapter(launcher);

        await Assert.ThrowsAsync<ArgumentException>(() => adapter.StartAsync(Request(
            WindowsDesktopCapabilities.OpenWebsiteSecure,
            new WindowsDesktopActionInput("OpenWebsite", "http://example.com"))));

        Assert.Empty(launcher.Targets);
    }

    [Fact]
    public async Task ExplicitExistingFileOpensExactPath()
    {
        var launcher = new RecordingLauncher();
        var adapter = CreateAdapter(launcher);
        var path = Path.Combine(Path.GetTempPath(), $"sg-file-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "fake test data");
        try
        {
            var result = await adapter.StartAsync(Request(
                WindowsDesktopCapabilities.OpenFile,
                new WindowsDesktopActionInput("OpenFile", path)));

            Assert.Equal(SkillExecutionStatus.Succeeded, result.Status);
            Assert.Equal(Path.GetFullPath(path), Assert.Single(launcher.Targets));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SearchRequiresHostTrustedWindowIdentityAndUsesAutomation()
    {
        var automation = new RecordingAutomation();
        var adapter = CreateAdapter(new RecordingLauncher(), automation);
        var identity = new ForegroundWindowSnapshot(
            42,
            "测试窗口",
            "test-process",
            420,
            new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow);

        await adapter.StartAsync(Request(
            WindowsDesktopCapabilities.SearchForeground,
            new WindowsDesktopActionInput(
                "SearchForeground",
                "测试内容",
                42,
                "测试窗口",
                WindowIdentity: identity)));
        var missingIdentity = await Assert.ThrowsAsync<WindowIdentityException>(() => adapter.StartAsync(Request(
            WindowsDesktopCapabilities.SearchForeground,
            new WindowsDesktopActionInput("SearchForeground", "不能执行"))));

        Assert.Equal(identity, automation.WindowIdentity);
        Assert.Equal("测试内容", automation.Query);
        Assert.Equal(WindowIdentityErrorCodes.Missing, missingIdentity.Code);
    }

    [Fact]
    public async Task ExplicitBrowserWebsiteUsesVisibleBrowserLaunch()
    {
        var launcher = new RecordingLauncher();
        var adapter = CreateAdapter(launcher);

        var result = await adapter.StartAsync(Request(
            WindowsDesktopCapabilities.OpenWebsiteInApplication,
            new WindowsDesktopActionInput(
                "OpenWebsiteInApplication",
                "chrome-app",
                Argument: "https://www.douyin.com/")));

        Assert.Equal(SkillExecutionStatus.Succeeded, result.Status);
        Assert.Equal("chrome.exe", launcher.VisibleBrowserTarget);
        Assert.Equal("https://www.douyin.com/", launcher.VisibleWebsite?.AbsoluteUri);
    }

    private static WindowsDesktopSkillAdapter CreateAdapter(
        RecordingLauncher launcher,
        RecordingAutomation? automation = null) =>
        new(launcher, new FakeCatalog(), automation ?? new RecordingAutomation());

    private static SkillInvocationRequest Request(
        string capability,
        WindowsDesktopActionInput input) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "windows.safe-launch",
            capability,
            JsonSerializer.Serialize(input),
            [new SkillResourceScope("Test", null, "explicit-target", "Execute")],
            SkillAuthorizationOrigin.ExplicitUser,
            Guid.NewGuid());

    private sealed class FakeCatalog : IInstalledApplicationCatalog
    {
        private static readonly KnownDesktopApplication Application =
            new("test-app", "测试应用", "test.exe");
        private static readonly KnownDesktopApplication Chrome =
            new("chrome-app", "Google Chrome", "chrome.exe");

        public IReadOnlyList<KnownDesktopApplication> GetApplications() => [Application, Chrome];

        public KnownDesktopApplication? FindById(string id) =>
            id == Application.Id ? Application : id == Chrome.Id ? Chrome : null;

        public KnownDesktopApplication? FindByDisplayName(string displayName) =>
            displayName == Application.DisplayName ? Application : null;

        public KnownDesktopApplication? FindBrowser(string browserName) =>
            browserName.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ? Chrome : null;
    }

    private sealed class RecordingLauncher : IDesktopProcessLauncher
    {
        public List<string> Targets { get; } = [];

        public string? VisibleBrowserTarget { get; private set; }

        public Uri? VisibleWebsite { get; private set; }

        public int? Start(string target)
        {
            Targets.Add(target);
            return 123;
        }

        public VisibleDesktopLaunchResult OpenApplicationVisible(string applicationLaunchTarget)
        {
            Targets.Add(applicationLaunchTarget);
            return new VisibleDesktopLaunchResult(123, 41, "测试应用");
        }

        public VisibleDesktopLaunchResult OpenWebsiteVisible(
            string? browserLaunchTarget,
            Uri website)
        {
            VisibleBrowserTarget = browserLaunchTarget;
            VisibleWebsite = website;
            Targets.Add(website.AbsoluteUri);
            return new VisibleDesktopLaunchResult(123, 42, "测试浏览器");
        }
    }

    private sealed class RecordingAutomation : IReliableDesktopAutomation
    {
        public ForegroundWindowSnapshot? WindowIdentity { get; private set; }

        public string? Query { get; private set; }

        public DesktopAutomationResult Search(ForegroundWindowSnapshot expectedWindow, string query)
        {
            WindowIdentity = expectedWindow;
            Query = query;
            return new DesktopAutomationResult(true, "已提交");
        }

        public DesktopAutomationResult Describe(ForegroundWindowSnapshot expectedWindow) =>
            new(true, "已读取");
    }
}
