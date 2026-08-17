using ScreenGuide.Core.Tasking;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed class LocalDeviceInitializer(
    ILocalTaskStore store,
    TimeProvider timeProvider)
{
    public async Task<DeviceRecord> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var existing = await store.GetLocalHostDeviceAsync(cancellationToken).ConfigureAwait(false);
        var device = existing is null
            ? new DeviceRecord
            {
                Id = Guid.NewGuid(),
                DisplayName = GetDisplayName(),
                DeviceType = DeviceType.WindowsHost,
                TrustState = DeviceTrustState.Local,
                CreatedAtUtc = now,
                LastSeenAtUtc = now
            }
            : existing with
            {
                DisplayName = GetDisplayName(),
                LastSeenAtUtc = now
            };

        await store.UpsertDeviceAsync(device, cancellationToken).ConfigureAwait(false);
        return device;
    }

    private static string GetDisplayName() =>
        string.IsNullOrWhiteSpace(Environment.MachineName)
            ? "Windows Desktop"
            : Environment.MachineName;
}
