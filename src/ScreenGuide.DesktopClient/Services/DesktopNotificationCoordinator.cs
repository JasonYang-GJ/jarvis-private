using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Services;

public sealed class DesktopNotificationCoordinator(
    TrayIconService tray,
    DesktopClientSettingsStore settingsStore)
{
    public async Task<DesktopClientSettings> ProcessAsync(
        IReadOnlyList<TaskSummaryDto> tasks,
        DesktopClientSettings settings,
        CancellationToken cancellationToken = default)
    {
        tray.UpdateState(TrayStateResolver.Resolve(true, tasks));
        if (!settings.NotificationsEnabled)
        {
            return settings;
        }

        var delivered = new HashSet<string>(
            settings.DeliveredNotificationKeys,
            StringComparer.Ordinal);
        var notifications = NotificationPlanner.Plan(tasks, delivered);
        if (!settings.NotificationStateInitialized)
        {
            foreach (var notification in notifications)
            {
                delivered.Add(notification.Key);
            }

            var initialized = settings with
            {
                NotificationStateInitialized = true,
                DeliveredNotificationKeys = delivered.TakeLast(500).ToHashSet(StringComparer.Ordinal)
            };
            await settingsStore.SaveAsync(initialized, cancellationToken).ConfigureAwait(false);
            return initialized;
        }

        if (notifications.Count == 0)
        {
            return settings;
        }

        foreach (var notification in notifications)
        {
            tray.ShowNotification(notification);
            delivered.Add(notification.Key);
        }

        var trimmed = delivered.TakeLast(500).ToHashSet(StringComparer.Ordinal);
        var updated = settings with { DeliveredNotificationKeys = trimmed };
        await settingsStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }
}
