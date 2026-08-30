using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.Agent.Abstractions;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Conversations;
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
    public async Task HostRegistersQwenBesideExistingProvidersWithoutChangingConversationOrProgrammingAgentOwners()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();

        var providers = host.Services.GetServices<IChatModelProvider>().ToArray();
        Assert.Equal(["codex", "deepseek", "qwen"], providers
            .Select(provider => provider.Descriptor.ProviderId)
            .Order(StringComparer.Ordinal)
            .ToArray());
        var qwen = providers.Single(provider => provider.Descriptor.ProviderId == "qwen");
        Assert.Equal("千问", qwen.Descriptor.DisplayName);
        Assert.Equal("qwen3.7-plus", Assert.Single(qwen.Descriptor.Models).ModelId);
        Assert.IsType<RoutedConversationProvider>(
            host.Services.GetRequiredService<IConversationProvider>());
        Assert.Equal("codex", host.Services.GetRequiredService<IAgentConnector>().ConnectorId);
    }

    [Fact]
    public async Task SessionProjectionReadsHaveOneDedicatedReadOnlyOwner()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        Assert.NotNull(host.Services.GetRequiredService<SessionProjectionService>());

        var repositoryRoot = FindRepositoryRoot();
        var coordinator = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "ScreenGuide.DesktopHost",
            "Runtime",
            "SessionCoordinator.cs"));
        var projection = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "ScreenGuide.DesktopHost",
            "Runtime",
            "SessionProjectionService.cs"));

        Assert.DoesNotContain("conversationStore.GetMessagesPageAsync", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionChangeJournal _changeJournal", coordinator, StringComparison.Ordinal);
        Assert.Contains("GetActiveTurnsAsync(\n                session.Id,\n                SessionTurnProjection.MaximumTurnViews", projection.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("SessionChangeJournal _changeJournal", projection, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionCoordinatorUsesOneBoundedOperationGateRegistry()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();

        var registry = host.Services.GetRequiredService<SessionOperationGateRegistry>();
        Assert.Same(registry, host.Services.GetRequiredService<SessionOperationGateRegistry>());

        var repositoryRoot = FindRepositoryRoot();
        var runtimeDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "ScreenGuide.DesktopHost",
            "Runtime");
        var coordinator = File.ReadAllText(Path.Combine(runtimeDirectory, "SessionCoordinator.cs"));
        var runtimeSources = Directory.EnumerateFiles(runtimeDirectory, "*.cs")
            .Select(File.ReadAllText)
            .ToArray();
        Assert.Contains("SessionOperationGateRegistry operationGates", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("ConcurrentDictionary<Guid, SemaphoreSlim>", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("_sessionGates", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("_turnGates", coordinator, StringComparison.Ordinal);
        Assert.Single(runtimeSources, source => source.Contains(
            "public ValueTask<IAsyncDisposable> AcquireSessionAsync(",
            StringComparison.Ordinal));
        Assert.Single(runtimeSources, source => source.Contains(
            "public ValueTask<IAsyncDisposable> AcquireTurnAsync(",
            StringComparison.Ordinal));
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

        var startupException = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync());
        Assert.Contains("Agent Connector ID", startupException.ToString(), StringComparison.Ordinal);
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
