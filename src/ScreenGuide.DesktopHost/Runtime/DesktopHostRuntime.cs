using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Ai;
using ScreenGuide.Core.Sessions;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Evidence;
using ScreenGuide.Persistence.Runtime;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed class DesktopHostRuntime(
    ILocalTaskStore store,
    IConversationStore conversationStore,
    ISessionStore sessionStore,
    IAiInvocationStore aiInvocationStore,
    LocalDeviceInitializer deviceInitializer,
    TaskRecoveryService recoveryService,
    TaskCancellationService cancellationService,
    TaskCancellationRegistry cancellationRegistry,
    AgentConnectorRegistry connectorRegistry,
    SkillAdapterRegistry skillAdapterRegistry,
    AgentTaskExecutionService executionService,
    ConversationService conversationService,
    SessionCoordinator sessionCoordinator,
    TaskEvidenceService evidenceService,
    RuntimeDataMaintenance maintenance,
    DesktopHostState state,
    TimeProvider timeProvider,
    ILogger<DesktopHostRuntime> logger)
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    public TaskCancellationService CancellationService => cancellationService;

    public TaskCancellationRegistry CancellationRegistry => cancellationRegistry;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var storeInitialized = false;
        DeviceRecord? localDevice = null;
        try
        {
            if (state.Snapshot.IsStarted)
            {
                return;
            }

            maintenance.CleanupStaleFiles();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            storeInitialized = true;
            await conversationStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await sessionStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await aiInvocationStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var invocationRecovery = await aiInvocationStore.InterruptRunningAsync(
                timeProvider.GetUtcNow(),
                "host_restarted",
                cancellationToken).ConfigureAwait(false);
            localDevice = await deviceInitializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await AppendAuditAsync(
                "HostStarting",
                localDevice.Id,
                AuditOutcome.Success,
                null,
                cancellationToken).ConfigureAwait(false);

            var projects = await store.GetAuthorizedProjectsAsync(cancellationToken).ConfigureAwait(false);
            var recovery = await recoveryService.RecoverAsync(
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            var conversationRecovery = await conversationStore.RecoverInterruptedAsync(
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            var sessionRecovery = await sessionCoordinator.RecoverAsync(cancellationToken)
                .ConfigureAwait(false);
            await evidenceService.FinalizeRecoveredTasksAsync(
                    recovery.InterruptedTaskIds,
                    cancellationToken)
                .ConfigureAwait(false);
            connectorRegistry.Initialize();
            skillAdapterRegistry.Initialize();
            var connectorIds = connectorRegistry.ConnectorIds;
            var skillIds = skillAdapterRegistry.SkillIds;
            state.MarkStarted(localDevice, projects, recovery.InterruptedTaskIds, connectorIds);
            await AppendAuditAsync(
                "HostStarted",
                localDevice.Id,
                AuditOutcome.Success,
                JsonSerializer.Serialize(new
                {
                    authorizedProjectCount = projects.Count,
                    recoveredTaskCount = recovery.InterruptedTaskIds.Count,
                    recoveredConversationCount = conversationRecovery.InterruptedConversationIds.Count,
                    recoveredSessionTurnCount = sessionRecovery.InterruptedTurnIds.Count,
                    recoveredAiInvocationCount = invocationRecovery.InterruptedInvocationIds.Count,
                    connectorCount = connectorIds.Count,
                    skillCount = skillIds.Count
                }),
                cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Desktop Host started with {ProjectCount} authorized projects and {ConnectorCount} connectors.",
                projects.Count,
                connectorIds.Count);
        }
        catch (Exception exception)
        {
            state.MarkStopped();
            logger.LogCritical(exception, "Desktop Host startup failed.");
            if (storeInitialized)
            {
                await TryAppendFailureAuditAsync(
                    "HostStartupFailed",
                    localDevice?.Id,
                    exception,
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = state.Snapshot;
            if (!snapshot.IsStarted)
            {
                return;
            }

            Exception? shutdownFailure = null;
            try
            {
                await executionService.StopAsync(cancellationToken).ConfigureAwait(false);
                await sessionCoordinator.StopAsync(cancellationToken).ConfigureAwait(false);
                await conversationService.StopAsync(cancellationToken).ConfigureAwait(false);
                await AppendAuditAsync(
                    "HostStopping",
                    snapshot.LocalDevice?.Id,
                    AuditOutcome.Success,
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                shutdownFailure = exception;
            }
            finally
            {
                cancellationRegistry.Dispose();
                state.MarkStopped();
            }

            if (shutdownFailure is null)
            {
                try
                {
                    await AppendAuditAsync(
                        "HostStopped",
                        snapshot.LocalDevice?.Id,
                        AuditOutcome.Success,
                        null,
                        cancellationToken).ConfigureAwait(false);
                    logger.LogInformation("Desktop Host stopped.");
                    return;
                }
                catch (Exception exception)
                {
                    shutdownFailure = exception;
                }
            }

            logger.LogError(shutdownFailure, "Desktop Host shutdown failed.");
            await TryAppendFailureAuditAsync(
                "HostShutdownFailed",
                snapshot.LocalDevice?.Id,
                shutdownFailure,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private Task AppendAuditAsync(
        string action,
        Guid? actorDeviceId,
        AuditOutcome outcome,
        string? detailsJson,
        CancellationToken cancellationToken) =>
        store.AppendAuditAsync(
            new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                OccurredAtUtc = timeProvider.GetUtcNow(),
                ActorDeviceId = actorDeviceId,
                Action = action,
                EntityType = "DesktopHost",
                EntityId = actorDeviceId?.ToString("D") ?? "local-host",
                Outcome = outcome,
                DetailsJson = detailsJson
            },
            cancellationToken);

    private async Task TryAppendFailureAuditAsync(
        string action,
        Guid? actorDeviceId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await AppendAuditAsync(
                action,
                actorDeviceId,
                AuditOutcome.Failed,
                JsonSerializer.Serialize(new
                {
                    exceptionType = SensitiveDataSanitizer.ExceptionType(exception)
                }),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception auditException)
        {
            logger.LogError(auditException, "Failed to record Desktop Host failure audit.");
        }
    }
}
