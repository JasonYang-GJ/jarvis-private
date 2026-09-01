using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Windows;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class DesktopActionEntryServiceTests
{
    [Fact]
    public async Task ConfirmedAllowlistedApplicationRunsOnceAndIsAudited()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var launcher = new RecordingLauncher();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IDesktopProcessLauncher>(launcher));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var applications = await client.ListDesktopApplicationsAsync();
        var first = await client.ExecuteDesktopActionAsync(new ExecuteDesktopActionRequestDto(
            "OpenApplication",
            "notepad",
            true,
            "open-notepad-once"));
        var repeated = await client.ExecuteDesktopActionAsync(new ExecuteDesktopActionRequestDto(
            "OpenApplication",
            "notepad",
            true,
            "open-notepad-once"));
        await using var store = await environment.OpenStoreAsync();
        var audits = await store.GetAuditLogAsync();
        var commands = await store.GetTaskCommandsAsync(Guid.Empty);
        await host.StopAsync();

        Assert.Contains(applications, item => item.Id == "notepad");
        Assert.True(first.Succeeded);
        Assert.False(first.WasDuplicate);
        Assert.True(repeated.WasDuplicate);
        Assert.Single(launcher.Targets);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"),
            launcher.Targets[0]);
        Assert.Contains(audits, item =>
            item.Action == "DesktopActionAuthorizationAllowed"
            && item.Outcome == AuditOutcome.Success);
        Assert.Contains(audits, item =>
            item.Action == "DesktopActionExecuted"
            && item.Outcome == AuditOutcome.Success);
        Assert.Empty(commands);
    }

    [Fact]
    public async Task MissingConfirmationUnknownApplicationAndHttpWebsiteNeverLaunch()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var launcher = new RecordingLauncher();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IDesktopProcessLauncher>(launcher));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var notConfirmed = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ExecuteDesktopActionAsync(new ExecuteDesktopActionRequestDto(
                "OpenApplication", "notepad", false)));
        var unknown = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ExecuteDesktopActionAsync(new ExecuteDesktopActionRequestDto(
                "OpenApplication", "powershell", true)));
        var insecureWebsite = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ExecuteDesktopActionAsync(new ExecuteDesktopActionRequestDto(
                "OpenWebsite", "http://example.com", true)));
        await host.StopAsync();

        Assert.Equal("desktop_action_not_authorized", notConfirmed.Error.Code);
        Assert.Equal("desktop_action_not_authorized", unknown.Error.Code);
        Assert.Equal("input_invalid", insecureWebsite.Error.Code);
        Assert.Empty(launcher.Targets);
    }

    [Fact]
    public async Task WebsiteQueryAndFragmentAreNotPersistedInCommandOrAudit()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var launcher = new RecordingLauncher();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IDesktopProcessLauncher>(launcher));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        const string sensitiveUrl = "https://example.com/guide?token=fake-secret#private";

        var result = await client.ExecuteDesktopActionAsync(new ExecuteDesktopActionRequestDto(
            "OpenWebsite",
            sensitiveUrl,
            true,
            "open-safe-website"));
        await using var store = await environment.OpenStoreAsync();
        var command = await store.GetCommandAsync(result.CommandId);
        var audits = await store.GetAuditLogAsync();
        await host.StopAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(sensitiveUrl, Assert.Single(launcher.Targets));
        Assert.DoesNotContain("fake-secret", command!.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-secret", string.Join('\n', audits.Select(item => item.DetailsJson)));
        Assert.Contains("https://example.com/guide", command.PayloadJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SearchForeground", "测试内容")]
    [InlineData("DescribeForeground", "9211")]
    public async Task DirectIpcWindowActionCannotTreatClientHandleAsTrustedIdentity(
        string actionKind,
        string target)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var automation = new RecordingAutomation();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IReliableDesktopAutomation>(automation));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var failure = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ExecuteDesktopActionAsync(new ExecuteDesktopActionRequestDto(
                actionKind,
                target,
                true,
                $"direct-ipc-{actionKind}",
                9211,
                "客户端声称的窗口")));
        await host.StopAsync();

        Assert.Equal(WindowIdentityErrorCodes.Missing, failure.Error.Code);
        Assert.Equal(0, automation.CallCount);
    }

    private sealed class RecordingLauncher : IDesktopProcessLauncher
    {
        public List<string> Targets { get; } = [];

        public int? Start(string target)
        {
            Targets.Add(target);
            return 4242;
        }

        public VisibleDesktopLaunchResult OpenApplicationVisible(string applicationLaunchTarget)
        {
            Targets.Add(applicationLaunchTarget);
            return new VisibleDesktopLaunchResult(4242, 87, "测试应用");
        }

        public VisibleDesktopLaunchResult OpenWebsiteVisible(
            string? browserLaunchTarget,
            Uri website)
        {
            Targets.Add(website.AbsoluteUri);
            return new VisibleDesktopLaunchResult(4242, 88, "测试浏览器");
        }
    }

    private sealed class RecordingAutomation : IReliableDesktopAutomation
    {
        public int CallCount { get; private set; }

        public DesktopAutomationResult Search(ForegroundWindowSnapshot expectedWindow, string query)
        {
            CallCount++;
            return new DesktopAutomationResult(true, "不应执行");
        }

        public DesktopAutomationResult Describe(ForegroundWindowSnapshot expectedWindow)
        {
            CallCount++;
            return new DesktopAutomationResult(true, "不应执行");
        }
    }
}
