using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.Agent.Abstractions;
using ScreenGuide.Core.Security;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.Skills.Abstractions;

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
    public void DesktopClientDependsOnlyOnNeutralDesktopProtocol()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectFile = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "ScreenGuide.DesktopClient",
            "ScreenGuide.DesktopClient.csproj"));

        Assert.Contains("ScreenGuide.DesktopProtocol", projectFile, StringComparison.Ordinal);
        Assert.DoesNotContain("ScreenGuide.DesktopHost", projectFile, StringComparison.Ordinal);
        Assert.DoesNotContain("ScreenGuide.Agent.Codex", projectFile, StringComparison.Ordinal);
        Assert.DoesNotContain("ScreenGuide.Persistence", projectFile, StringComparison.Ordinal);
        Assert.DoesNotContain("ScreenGuide.Core", projectFile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostRegistersV02PolicyAndBuiltInSkillAdaptersWithoutChangingClientBoundary()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();

        Assert.NotNull(host.Services.GetRequiredService<CapabilityPolicyEngine>());
        var skillIds = host.Services.GetServices<ISkillAdapter>()
            .Select(skill => skill.Descriptor.Id)
            .ToArray();
        Assert.Equal(2, skillIds.Length);
        Assert.Contains("codex.project-task", skillIds);
        Assert.Contains("windows.safe-launch", skillIds);
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ScreenGuide.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("找不到仓库根目录。");
    }
}
