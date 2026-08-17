using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.Persistence.Runtime;
using AgentTaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class DesktopHostRuntimeTests
{
    [Fact]
    public async Task StartsAndStopsWithoutWpfAndRecordsLifecycleAudit()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();

        await host.StartAsync();
        var state = host.Services.GetRequiredService<DesktopHostState>();
        var runtime = host.Services.GetRequiredService<DesktopHostRuntime>();
        var registry = host.Services.GetRequiredService<AgentConnectorRegistry>();

        Assert.True(state.Snapshot.IsStarted);
        Assert.NotNull(state.Snapshot.LocalDevice);
        Assert.Equal(DeviceTrustState.Local, state.Snapshot.LocalDevice.TrustState);
        Assert.Empty(state.Snapshot.AuthorizedProjects);
        Assert.Equal(["codex"], registry.ConnectorIds);
        Assert.Same(
            host.Services.GetRequiredService<TaskCancellationService>(),
            runtime.CancellationService);
        Assert.Same(
            host.Services.GetRequiredService<TaskCancellationRegistry>(),
            runtime.CancellationRegistry);

        await host.StopAsync();
        Assert.False(state.Snapshot.IsStarted);
        await using var store = await environment.OpenStoreAsync();
        var audit = await store.GetAuditLogAsync();
        AssertLifecycleOrder(audit, "HostStarting", "HostStarted", "HostStopping", "HostStopped");
    }

    [Fact]
    public async Task ReusesSameLocalDeviceAcrossHostRestarts()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        Guid firstDeviceId;
        using (var firstHost = environment.BuildHost())
        {
            await firstHost.StartAsync();
            firstDeviceId = firstHost.Services
                .GetRequiredService<DesktopHostState>()
                .Snapshot.LocalDevice!.Id;
            await firstHost.StopAsync();
        }

        environment.TimeProvider.UtcNow = environment.TimeProvider.UtcNow.AddMinutes(1);
        using var secondHost = environment.BuildHost();
        await secondHost.StartAsync();
        var secondDevice = secondHost.Services
            .GetRequiredService<DesktopHostState>()
            .Snapshot.LocalDevice;
        await secondHost.StopAsync();

        Assert.NotNull(secondDevice);
        Assert.Equal(firstDeviceId, secondDevice.Id);
        Assert.Equal(environment.TimeProvider.UtcNow, secondDevice.LastSeenAtUtc);
    }

    [Fact]
    public async Task LoadsOnlyAuthorizedProjects()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, authorized, revoked) = await environment.SeedProjectsAsync();
        using var host = environment.BuildHost();

        await host.StartAsync();
        var projects = host.Services
            .GetRequiredService<DesktopHostState>()
            .Snapshot.AuthorizedProjects;
        await host.StopAsync();

        var project = Assert.Single(projects);
        Assert.Equal(authorized.Id, project.Id);
        Assert.DoesNotContain(projects, item => item.Id == revoked.Id);
    }

    [Fact]
    public async Task StartupRecoveryMarksRunningTaskInterruptedWithoutRestartingIt()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var taskId = await environment.SeedRunningTaskAsync();
        using var host = environment.BuildHost();

        await host.StartAsync();
        var snapshot = host.Services.GetRequiredService<DesktopHostState>().Snapshot;
        var store = host.Services.GetRequiredService<ILocalTaskStore>();
        var recoveredTask = await store.GetTaskAsync(taskId);
        await host.StopAsync();

        Assert.Contains(taskId, snapshot.RecoveredTaskIds);
        Assert.NotNull(recoveredTask);
        Assert.Equal(AgentTaskStatus.Interrupted, recoveredTask.Status);
    }

    private static void AssertLifecycleOrder(
        IReadOnlyList<AuditLogEntry> audit,
        params string[] expectedActions)
    {
        var lifecycleActions = audit
            .Where(entry => entry.EntityType == "DesktopHost")
            .Select(entry => entry.Action)
            .ToArray();
        Assert.Equal(expectedActions, lifecycleActions);
    }
}
