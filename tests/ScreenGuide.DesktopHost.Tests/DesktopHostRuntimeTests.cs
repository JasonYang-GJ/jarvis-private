using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Ai;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.Persistence.Runtime;
using ScreenGuide.Persistence.Sqlite;
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

    [Fact]
    public async Task StartupInterruptsRunningAiInvocationsBeforeAcceptingWorkWithoutReplay()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        await using (var schemaStore = new SqliteTaskStore(environment.Options.DatabasePath))
        {
            await schemaStore.InitializeAsync();
        }

        var innerStore = new SqliteAiInvocationStore(environment.Options.DatabasePath);
        await innerStore.InitializeAsync();
        var invocationId = Guid.NewGuid();
        var startedAt = environment.TimeProvider.GetUtcNow().AddMinutes(-1);
        await innerStore.StartAsync(new AiInvocationRecord
        {
            Id = invocationId,
            Purpose = AiInvocationPurpose.SemanticIntent,
            ProviderId = "startup-provider",
            ModelId = "startup-model",
            PromptId = "intent.semantic",
            PromptVersion = "1",
            PromptContentHash = "sha256:startup-test",
            DataDestination = "startup test destination",
            Status = AiInvocationStatus.Running,
            StartedAtUtc = startedAt
        });
        var provider = new StartupCountingChatProvider();
        var firstTrackingStore = new TrackingAiInvocationStore(innerStore)
        {
            PauseRecovery = true
        };
        using (var firstHost = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiInvocationStore>(firstTrackingStore);
            services.AddSingleton(new ChatProviderRegistry([provider]));
        }))
        {
            var state = firstHost.Services.GetRequiredService<DesktopHostState>();
            var starting = firstHost.StartAsync();
            await firstTrackingStore.RecoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try
            {
                Assert.False(state.Snapshot.IsStarted);
            }
            finally
            {
                firstTrackingStore.AllowRecovery.TrySetResult();
            }

            await starting;
            var recovered = await innerStore.GetAsync(invocationId);

            Assert.True(state.Snapshot.IsStarted);
            Assert.NotNull(recovered);
            Assert.Equal(AiInvocationStatus.Interrupted, recovered.Status);
            Assert.Equal(environment.TimeProvider.GetUtcNow(), recovered.CompletedAtUtc);
            Assert.Equal("host_restarted", recovered.FailureCode);
            Assert.Equal([invocationId], Assert.Single(
                firstTrackingStore.RecoveryResults).InterruptedInvocationIds);
            Assert.Equal(0, firstTrackingStore.StartCount);
            Assert.Equal(0, provider.CompleteCount);

            await firstHost.Services.GetRequiredService<DesktopHostRuntime>().StartAsync();
            Assert.Single(firstTrackingStore.RecoveryResults);
            await firstHost.StopAsync();
        }

        var firstCompletedAt = (await innerStore.GetAsync(invocationId))?.CompletedAtUtc;
        environment.TimeProvider.UtcNow = environment.TimeProvider.UtcNow.AddMinutes(1);
        var secondTrackingStore = new TrackingAiInvocationStore(innerStore);
        using var secondHost = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiInvocationStore>(secondTrackingStore);
            services.AddSingleton(new ChatProviderRegistry([provider]));
        });
        await secondHost.StartAsync();
        var recoveredAgain = await innerStore.GetAsync(invocationId);
        await secondHost.StopAsync();

        Assert.Empty(Assert.Single(secondTrackingStore.RecoveryResults).InterruptedInvocationIds);
        Assert.Equal(firstCompletedAt, recoveredAgain?.CompletedAtUtc);
        Assert.Equal(AiInvocationStatus.Interrupted, recoveredAgain?.Status);
        Assert.Equal(0, secondTrackingStore.StartCount);
        Assert.Equal(0, provider.CompleteCount);
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

    private sealed class TrackingAiInvocationStore(IAiInvocationStore inner) : IAiInvocationStore
    {
        public int StartCount { get; private set; }

        public bool PauseRecovery { get; init; }

        public TaskCompletionSource RecoveryEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowRecovery { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<AiInvocationRecoveryResult> RecoveryResults { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task StartAsync(
            AiInvocationRecord invocation,
            CancellationToken cancellationToken = default)
        {
            StartCount++;
            return inner.StartAsync(invocation, cancellationToken);
        }

        public Task<AiInvocationTransitionResult> CompleteAsync(
            Guid invocationId,
            string finishReason,
            AiTokenUsage? usage,
            string? providerRequestId,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken = default) =>
            inner.CompleteAsync(
                invocationId,
                finishReason,
                usage,
                providerRequestId,
                completedAtUtc,
                cancellationToken);

        public Task<AiInvocationTransitionResult> FailAsync(
            Guid invocationId,
            AiInvocationStatus status,
            string failureCode,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken = default) =>
            inner.FailAsync(
                invocationId,
                status,
                failureCode,
                completedAtUtc,
                cancellationToken);

        public async Task<AiInvocationRecoveryResult> InterruptRunningAsync(
            DateTimeOffset interruptedAtUtc,
            string failureCode,
            CancellationToken cancellationToken = default)
        {
            RecoveryEntered.TrySetResult();
            if (PauseRecovery)
            {
                await AllowRecovery.Task.WaitAsync(cancellationToken);
            }

            var result = await inner.InterruptRunningAsync(
                interruptedAtUtc,
                failureCode,
                cancellationToken);
            RecoveryResults.Add(result);
            return result;
        }

        public Task<AiInvocationRecord?> GetAsync(
            Guid invocationId,
            CancellationToken cancellationToken = default) =>
            inner.GetAsync(invocationId, cancellationToken);

        public Task<IReadOnlyList<AiInvocationRecord>> GetForConversationTurnAsync(
            Guid conversationTurnId,
            CancellationToken cancellationToken = default) =>
            inner.GetForConversationTurnAsync(conversationTurnId, cancellationToken);

        public Task<IReadOnlyList<AiInvocationRecord>> GetForSessionTurnAsync(
            Guid sessionTurnId,
            CancellationToken cancellationToken = default) =>
            inner.GetForSessionTurnAsync(sessionTurnId, cancellationToken);
    }

    private sealed class StartupCountingChatProvider : IChatModelProvider
    {
        public ChatProviderDescriptor Descriptor { get; } = new(
            "startup-provider",
            "Startup Provider",
            "startup test destination",
            false,
            [new ChatModelDescriptor(
                "startup-model",
                "Startup Model",
                ChatModelCapabilities.None)]);

        public int CompleteCount { get; private set; }

        public Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            CompleteCount++;
            throw new InvalidOperationException("Host 启动恢复不得调用 Provider。");
        }

        public Task<ChatProviderHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Host 启动恢复不得检查 Provider 健康。");

        public Task CancelAsync(
            Guid turnId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Host 启动恢复不得调用 Provider 取消。");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
