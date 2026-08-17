using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.Core.Tasking;

namespace ScreenGuide.DesktopHost.Runtime;

public static class HostFailureRecorder
{
    public static async Task TryRecordUnhandledAsync(
        IServiceProvider services,
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            var store = services.GetService<ILocalTaskStore>();
            if (store is null)
            {
                return;
            }

            var state = services.GetService<DesktopHostState>()?.Snapshot;
            var actorDeviceId = state?.LocalDevice?.Id;
            await store.AppendAuditAsync(
                new AuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    ActorDeviceId = actorDeviceId,
                    Action = "HostUnhandledException",
                    EntityType = "DesktopHost",
                    EntityId = actorDeviceId?.ToString("D") ?? "local-host",
                    Outcome = AuditOutcome.Failed,
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        exceptionType = exception.GetType().FullName,
                        exception.Message
                    })
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception auditException)
        {
            Console.Error.WriteLine(
                $"Desktop Host could not record unhandled exception audit: {auditException.GetType().Name}");
        }
    }
}
