using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.Skills.Abstractions;
using System.Runtime.CompilerServices;
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
        var invocations = await store.GetSkillInvocationsAsync(task.Id);
        var events = await store.GetTaskEventsAsync(task.Id);
        var audit = await store.GetAuditLogAsync();
        await host.StopAsync();

        Assert.Equal(AgentTaskStatus.Succeeded, persistedTask?.Status);
        Assert.False(string.IsNullOrWhiteSpace(reference.ExternalRunId));
        Assert.Equal(reference.ExternalRunId, run?.ExternalRunId);
        Assert.Equal(AgentRunStatus.Succeeded, run?.Status);
        Assert.Equal(AgentAttemptStatus.Completed, Assert.Single(attempts).Status);
        Assert.Equal(TaskPhase.Verifying, persistedTask?.Phase);
        var invocation = Assert.Single(invocations);
        Assert.Equal("codex.project-task", invocation.SkillId);
        Assert.Equal(SkillInvocationStatus.Succeeded, invocation.Status);
        Assert.Equal("skill/codex.project-task", run?.Transport);
        Assert.Contains(events, item => item.ToPhase == TaskPhase.Routing);
        Assert.Contains(events, item => item.ToPhase == TaskPhase.Executing);
        Assert.Contains(events, item => item.ToPhase == TaskPhase.Verifying);
        Assert.Contains(audit, item =>
            item.Action == "SkillAuthorizationAllowed"
            && item.EntityId == invocation.Id.ToString("D"));
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
            Path.Combine(environment.RootDirectory, "missing-codex.exe"),
            environment.Options.PipeName);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrematureOrExceptionalEventFeedPersistsInterruptedEvidenceBeforeFinalWake(
        bool throwFromFeed)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, _, task) = await environment.SeedTaskAsync("TEST_SUCCESS");
        var adapter = new EndingSkillAdapter(throwFromFeed);
        using var host = environment.BuildHost(services =>
        {
            services.RemoveAll<ISkillAdapter>();
            services.AddSingleton<ISkillAdapter>(adapter);
        });
        await host.StartAsync();
        var service = host.Services.GetRequiredService<AgentTaskExecutionService>();
        var store = host.Services.GetRequiredService<ILocalTaskStore>();

        _ = await service.StartTaskAsync(task.Id);
        await adapter.FeedStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        adapter.EndFeed();
        await service.WaitForTaskAsync(task.Id);

        var persistedTask = await store.GetTaskAsync(task.Id);
        var run = await store.GetAgentRunByTaskAsync(task.Id);
        var invocation = Assert.Single(await store.GetSkillInvocationsAsync(task.Id));
        var evidence = await store.GetTaskEvidenceAsync(task.Id);
        await host.StopAsync();

        Assert.Equal(AgentTaskStatus.Interrupted, persistedTask?.Status);
        Assert.Equal(AgentRunStatus.Interrupted, run?.Status);
        Assert.Equal("task_state_sync_failed", run?.FailureCode);
        Assert.Equal(SkillInvocationStatus.Interrupted, invocation.Status);
        Assert.NotNull(evidence);
        Assert.Equal(AgentTaskStatus.Interrupted, evidence!.TaskStatus);
    }

    [Fact]
    public async Task CancellationWinsRaceWithPrematureEventFeed()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (device, _, task) = await environment.SeedTaskAsync("TEST_SUCCESS");
        var adapter = new EndingSkillAdapter(throwFromFeed: false, endWhenCancelled: true);
        using var host = environment.BuildHost(services =>
        {
            services.RemoveAll<ISkillAdapter>();
            services.AddSingleton<ISkillAdapter>(adapter);
        });
        await host.StartAsync();
        var service = host.Services.GetRequiredService<AgentTaskExecutionService>();
        var store = host.Services.GetRequiredService<ILocalTaskStore>();

        _ = await service.StartTaskAsync(task.Id);
        await adapter.FeedStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(await service.CancelTaskAsync(task.Id, device.Id));
        var persistedTask = await store.GetTaskAsync(task.Id);
        var evidence = await store.GetTaskEvidenceAsync(task.Id);
        await host.StopAsync();

        Assert.Equal(AgentTaskStatus.Cancelled, persistedTask?.Status);
        Assert.NotNull(evidence);
        Assert.Equal(AgentTaskStatus.Cancelled, evidence!.TaskStatus);
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
        Assert.Equal(TaskPhase.AwaitingPermission, (await store.GetTaskAsync(task.Id))?.Phase);
        Assert.Equal(
            SkillInvocationStatus.WaitingForUser,
            Assert.Single(await store.GetSkillInvocationsAsync(task.Id)).Status);
        Assert.NotNull(await store.GetPendingDecisionRequestAsync(task.Id));

        var responseCommand = await environment.RegisterTaskCommandAsync(
            task.Id,
            CommandType.UserResponse,
            "{\"response\":\"TEST_CONTINUE\"}");
        var second = await service.ContinueTaskAsync(
            task.Id,
            "TEST_CONTINUE",
            responseCommand.Id);
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
        Assert.Equal(TaskPhase.Verifying, (await store.GetTaskAsync(task.Id))?.Phase);
        Assert.Equal(
            SkillInvocationStatus.Succeeded,
            Assert.Single(await store.GetSkillInvocationsAsync(task.Id)).Status);
        Assert.Equal(
            2,
            (await store.GetAuditLogAsync()).Count(item =>
                item.Action == "SkillAuthorizationAllowed"));
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
        var invocation = Assert.Single(await store.GetSkillInvocationsAsync(task.Id));
        await host.StopAsync();

        Assert.Equal(AgentTaskStatus.Cancelled, persistedTask?.Status);
        Assert.NotNull(Assert.Single(attempts).CancellationConfirmedAtUtc);
        Assert.Equal(SkillInvocationStatus.Cancelled, invocation.Status);
        Assert.Equal(TaskPhase.Verifying, persistedTask?.Phase);
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
            Assert.Equal(
                SkillInvocationStatus.Interrupted,
                Assert.Single(await store.GetSkillInvocationsAsync(task.Id)).Status);
            Assert.Equal(TaskPhase.Verifying, (await store.GetTaskAsync(task.Id))?.Phase);
            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.False(File.Exists(marker));
            await secondHost.StopAsync();
        }
    }

    [Fact]
    public async Task RevokedTaskResourceScopeIsRejectedBeforeSkillStarts()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, _, task) = await environment.SeedTaskAsync("TEST_SUCCESS");
        await using (var store = await environment.OpenStoreAsync())
        {
            var scope = Assert.Single(await store.GetResourceScopesAsync(task.Id));
            await store.UpsertResourceScopeAsync(scope with
            {
                RevokedAtUtc = environment.TimeProvider.GetUtcNow()
            });
        }

        using var host = environment.BuildHost();
        await host.StartAsync();
        var service = host.Services.GetRequiredService<AgentTaskExecutionService>();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.StartTaskAsync(task.Id));

        var activeStore = host.Services.GetRequiredService<ILocalTaskStore>();
        Assert.Null(await activeStore.GetAgentRunByTaskAsync(task.Id));
        Assert.Empty(await activeStore.GetSkillInvocationsAsync(task.Id));
        Assert.Equal(TaskPhase.Planning, (await activeStore.GetTaskAsync(task.Id))?.Phase);
        await host.StopAsync();
    }

    [Fact]
    public async Task UnrelatedCommandCannotAuthorizeSkillExecution()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, _, task) = await environment.SeedTaskAsync("TEST_SUCCESS");
        var wrongCommand = await environment.RegisterTaskCommandAsync(
            task.Id,
            CommandType.CancelTask);
        using var host = environment.BuildHost();
        await host.StartAsync();
        var service = host.Services.GetRequiredService<AgentTaskExecutionService>();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.StartTaskAsync(task.Id, wrongCommand.Id));

        var store = host.Services.GetRequiredService<ILocalTaskStore>();
        Assert.Null(await store.GetAgentRunByTaskAsync(task.Id));
        Assert.Empty(await store.GetSkillInvocationsAsync(task.Id));
        Assert.Equal(TaskPhase.Planning, (await store.GetTaskAsync(task.Id))?.Phase);
        await host.StopAsync();
    }

    private sealed class EndingSkillAdapter(
        bool throwFromFeed,
        bool endWhenCancelled = false) : ISkillAdapter
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FeedStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SkillDescriptor Descriptor { get; } = new(
            "codex.project-task",
            "test",
            "Bounded test adapter",
            IsBuiltIn: true,
            new HashSet<string>(StringComparer.Ordinal) { "coding.execute" });

        public Task<SkillStartResult> StartAsync(
            SkillInvocationRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(new SkillStartResult(
                new SkillRunReference(request.TaskId, request.InvocationId, "test-run", request.AttemptId),
                SkillExecutionStatus.Running,
                DateTimeOffset.UtcNow,
                "test",
                4242));

        public Task<SkillStatusSnapshot> GetStatusAsync(
            SkillRunReference run,
            CancellationToken cancellationToken = default) => Task.FromResult(new SkillStatusSnapshot(
                run,
                SkillExecutionStatus.Running,
                DateTimeOffset.UtcNow));

        public async IAsyncEnumerable<SkillEvent> GetEventsAsync(
            SkillRunReference run,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            FeedStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            if (throwFromFeed)
            {
                throw new IOException("synthetic event feed failure");
            }

            yield break;
        }

        public Task CancelAsync(
            SkillRunReference run,
            CancellationToken cancellationToken = default)
        {
            if (endWhenCancelled)
            {
                _release.TrySetResult();
            }

            return Task.CompletedTask;
        }

        public Task<SkillStartResult> RespondAsync(
            SkillDecisionResponse response,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SkillFinalResult?> GetFinalResultAsync(
            SkillRunReference run,
            CancellationToken cancellationToken = default) => Task.FromResult<SkillFinalResult?>(null);

        public void EndFeed() => _release.TrySetResult();
    }
}
