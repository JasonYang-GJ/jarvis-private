using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.Agent.Abstractions;
using ScreenGuide.DesktopHost.Runtime;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class DesktopHostDependencyTests
{
    [Fact]
    public void HostAssemblyDoesNotReferenceWpfOrLegacyApp()
    {
        var references = typeof(DesktopHostRuntime).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        Assert.DoesNotContain("PresentationFramework", references);
        Assert.DoesNotContain("PresentationCore", references);
        Assert.DoesNotContain("ScreenGuide.App", references);
    }

    [Fact]
    public async Task ConnectorConfigurationFailureIsAuditedAfterStoreInitialization()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAgentConnector>(
                new AgentConnectorRegistryTests.FakeConnector("codex"));
            services.AddSingleton<IAgentConnector>(
                new AgentConnectorRegistryTests.FakeConnector("CODEX"));
        });

        await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync());
        await using var store = await environment.OpenStoreAsync();
        var audit = await store.GetAuditLogAsync();

        Assert.Contains(audit, entry => entry.Action == "HostStarting");
        Assert.Contains(audit, entry =>
            entry.Action == "HostStartupFailed" && entry.Outcome == Core.Tasking.AuditOutcome.Failed);
    }
}
