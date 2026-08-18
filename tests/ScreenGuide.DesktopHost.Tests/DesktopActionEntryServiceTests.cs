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
        Assert.Equal("notepad.exe", launcher.Targets[0]);
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

    private sealed class RecordingLauncher : IDesktopProcessLauncher
    {
        public List<string> Targets { get; } = [];

        public int? Start(string target)
        {
            Targets.Add(target);
            return 4242;
        }
    }
}
