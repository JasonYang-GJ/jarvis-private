using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopHost.Runtime;
using AgentTaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class AgentTaskExecutionServiceTests
{
    [Fact]
    public async Task SuccessfulTurnPersistsThreadAndAuthoritativeCompletion()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, _, task) = await environment.SeedTaskAsync("TEST_SUCCESS");
        using var host = environment.BuildHost();
        await host.StartAsync();
        var service = host.Services.GetRequiredService<AgentTaskExecutionService>();

        var reference = await service.StartTaskAsync(task.Id);
        await service.WaitForTaskAsync(task.Id);

        var store = host.Services.GetRequiredService<ILocalTaskStore>();
        var persistedTask = await store.GetTaskAsync(task.Id);
        var run = await store.GetAgentRunByTaskAsync(task.Id);
        var attempts = await store.GetAgentAttemptsAsync(task.Id);
        await host.StopAsync();

        Assert.Equal(AgentTaskStatus.Succeeded, persistedTask?.Status);
        Assert.False(string.IsNullOrWhiteSpace(reference.ExternalRunId));
        Assert.Equal(reference.ExternalRunId, run?.ExternalRunId);
        Assert.Equal(AgentRunStatus.Succeeded, run?.Status);
        Assert.Equal(AgentAttemptStatus.Completed, Assert.Single(attempts).Status);
    }

    [Fact]
    public async Task ExplicitFailureIsPersistedAndNeverCompleted()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, _, task) = await environment.SeedTaskAsync("TEST_FAILURE");
        using var host = environment.BuildHost();
        await host.StartAsync();
        var service = host.Services.GetRequiredService<AgentTaskExecutionService>();

        await service.StartTaskAsync(task.Id);
        await service.WaitForTaskAsync(task.Id);

        var store = host.Services.GetRequiredService<ILocalTaskStore>();
        var persistedTask = await store.GetTaskAsync(task.Id);
        var events = await store.GetTaskEventsAsync(task.Id);
        await host.StopAsync();

        Assert.Equal(AgentTaskStatus.Failed, persistedTask?.Status);
        Assert.DoesNotContain(events, item => item.ToStatus == AgentTaskStatus.Succeeded);
    }

    [Fact]
    public async Task StartupFailureBeforeThreadIdIsPersistedAsFailed()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, _, task) = await environment.SeedTaskAsync("TEST_SUCCESS");
        var options = new DesktopHostOptions(
            environment.Options.DataDirectory,
            Path.Combine(environment.RootDirectory, "missing-codex.exe"));
        using var host = DesktopHostFactory.Build(
            Array.Empty<string>(),
            options,
            services => services.AddSingleton<TimeProvider>(environment.TimeProvider));
        await host.StartAsync();
        var service = host.Services.GetRequiredService<AgentTaskExecutionService>();

        var reference = await service.StartTaskAsync(task.Id);
        await service.WaitForTaskAsync(task.Id);

        var store = host.Services.GetRequiredService<ILocalTaskStore>();
        var persistedTask = await store.GetTaskAsync(task.Id);
        var run = await store.GetAgentRunByTaskAsync(task.Id);
        await host.StopAsync();

        Assert.Null(reference.ExternalRunId);
        Assert.Equal(AgentTaskStatus.Failed, persistedTask?.Status);
        Assert.Equal(AgentRunStatus.Failed, run?.Status);
        Assert.Null(run?.ExternalRunId);
    }

    [Fact]
    public async Task MissingTerminalEventIsPersistedAsInterrupted()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, _, task) = await environment.SeedTaskAsync("TEST_NO_TERMINAL");
        using var host = environment.BuildHost();
        await host.StartAsync();
        var service = host.Services.GetRequiredService<AgentTaskExecutionService>();

        await service.StartTaskAsync(task.Id);
        await service.WaitForTaskAsync(task.Id);

        var store = host.Services.GetRequiredService<ILocalTaskStore>();
        var persistedTask = await store.GetTaskAsync(task.Id);
        var run = await store.GetAgentRunByTaskAsync(task.Id);
        await host.StopAsync();

        Assert.Equal(AgentTaskStatus.Interrupted, persistedTask?.Status);
        Assert.Equal(AgentRunStatus.Interrupted, run?.Status);
    }

    [Fact]
    public async Task ActionRequiredContinuesSameThreadAndCreatesSecondAttempt()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, _, task) = await environment.SeedTaskAsync("TEST_ACTION_REQUIRED");
        using var host = environment.BuildHost();
        await host.StartAsync();
        var service = host.Services.GetRequiredService<AgentTaskExecutionService>();
        var store = host.Services.GetRequiredService<ILocalTaskStore>();

        var first = await service.StartTaskAsync(task.Id);
        await service.WaitForTaskAsync(task.Id);
        Assert.Equal(AgentTaskStatus.WaitingForUser, (await store.GetTaskAsync(task.Id))?.Status);
        Assert.NotNull(await store.GetPendingDecisionRequestAsync(task.Id));

        var second = await service.ContinueTaskAsync(task.Id, "TEST_CONTINUE");
        await service.WaitForTaskAsync(task.Id);
        var attempts = await store.GetAgentAttemptsAsync(task.Id);
        var run = await store.GetAgentRunByTaskAsync(task.Id);
        await host.StopAsync();

        Assert.Equal(first.ExternalRunId, second.ExternalRunId);
        Assert.Equal(first.ExternalRunId, run?.ExternalRunId);
        Assert.Equal(AgentTaskStatus.Succeeded, (await store.GetTaskAsync(task.Id))?.Status);
        Assert.Equal(2, attempts.Count);
        Assert.Equal(
            [AgentAttemptOperation.Start, AgentAttemptOperation.Resume],
            attempts.Select(item => item.Operation));
        Assert.Null(await store.GetPendingDecisionRequestAsync(task.Id));
    }

    [Fact]
    public async Task DuplicateConnectorEventDoesNotDuplicateStateChange()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, _, task) = await environment.SeedTaskAsync("TEST_DUPLICATE");
        using var host = environment.BuildHost();
        await host.StartAsync();
        var service = host.Services.GetRequiredService<AgentTaskExecutionService>();

        await service.StartTaskAsync(task.Id);
        await service.WaitForTaskAsync(task.Id);

        var store = host.Services.GetRequiredService<ILocalTaskStore>();
        var events = await store.GetTaskEventsAsync(task.Id);
        await host.StopAsync();

        Assert.Single(events, item => item.ToStatus == AgentTaskStatus.Succeeded);
    }

    [Fact]
    public async Task CancellationKillsChildAndPersistsCancelled()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var marker = Path.Combine(environment.RootDirectory, "cancelled-child-marker.txt");
        var (device, _, task) = await environment.SeedTaskAsync(
            $"TEST_LONG_RUNNING\nMARKER={marker}");
        using var host = environment.BuildHost();
        await host.StartAsync();
        var service = host.Services.GetRequiredService<AgentTaskExecutionService>();

        await service.StartTaskAsync(task.Id);
        await Task.Delay(500);
        Assert.True(await service.CancelTaskAsync(task.Id, device.Id));
        await Task.Delay(TimeSpan.FromSeconds(5));

        var store = host.Services.GetRequiredService<ILocalTaskStore>();
        var persistedTask = await store.GetTaskAsync(task.Id);
        var attempts = await store.GetAgentAttemptsAsync(task.Id);
        await host.StopAsync();

        Assert.Equal(AgentTaskStatus.Cancelled, persistedTask?.Status);
        Assert.NotNull(Assert.Single(attempts).CancellationConfirmedAtUtc);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task RevokedProjectCannotStartConnector()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, _, task) = await environment.SeedTaskAsync("TEST_SUCCESS", authorized: false);
        using var host = environment.BuildHost();
        await host.StartAsync();
        var service = host.Services.GetRequiredService<AgentTaskExecutionService>();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.StartTaskAsync(task.Id));

        var store = host.Services.GetRequiredService<ILocalTaskStore>();
        Assert.Null(await store.GetAgentRunByTaskAsync(task.Id));
        await host.StopAsync();
    }

    [Fact]
    public async Task HostStopInterruptsAttemptAndRestartDoesNotReplayInstruction()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var marker = Path.Combine(environment.RootDirectory, "restart-replay-marker.txt");
        var (_, _, task) = await environment.SeedTaskAsync(
            $"TEST_LONG_RUNNING\nMARKER={marker}");

        using (var firstHost = environment.BuildHost())
        {
            await firstHost.StartAsync();
            var service = firstHost.Services.GetRequiredService<AgentTaskExecutionService>();
            await service.StartTaskAsync(task.Id);
            await Task.Delay(500);
            await firstHost.StopAsync();
        }

        using (var secondHost = environment.BuildHost())
        {
            await secondHost.StartAsync();
            var store = secondHost.Services.GetRequiredService<ILocalTaskStore>();
            Assert.Equal(AgentTaskStatus.Interrupted, (await store.GetTaskAsync(task.Id))?.Status);
            Assert.False(string.IsNullOrWhiteSpace(
                (await store.GetAgentRunByTaskAsync(task.Id))?.ExternalRunId));
            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.False(File.Exists(marker));
            await secondHost.StopAsync();
        }
    }
}
