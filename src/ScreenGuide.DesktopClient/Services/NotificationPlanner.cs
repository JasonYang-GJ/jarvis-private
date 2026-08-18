using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Services;

public sealed record NotificationCandidate(
    string Key,
    Guid TaskId,
    string Title,
    string Message,
    string Kind);

public static class NotificationPlanner
{
    public static IReadOnlyList<NotificationCandidate> Plan(
        IEnumerable<TaskSummaryDto> tasks,
        ISet<string> deliveredKeys)
    {
        var results = new List<NotificationCandidate>();
        foreach (var task in tasks)
        {
            var kind = task.Status switch
            {
                "WaitingForUser" => "WaitingForUser",
                "Succeeded" => "Completed",
                "Failed" or "Interrupted" => "Failed",
                _ => null
            };
            if (kind is null || string.IsNullOrWhiteSpace(task.UserSummary))
            {
                continue;
            }

            var key = $"{task.Id:N}:{task.Status}:{task.UpdatedAtUtc.UtcTicks}";
            if (deliveredKeys.Contains(key))
            {
                continue;
            }

            var title = kind switch
            {
                "WaitingForUser" => "任务需要你的决定",
                "Failed" => "任务没有完成",
                _ => "任务已完成"
            };
            results.Add(new NotificationCandidate(
                key,
                task.Id,
                title,
                task.UserSummary,
                kind));
        }

        return results;
    }
}

public enum TrayVisualState
{
    Idle,
    Working,
    WaitingForUser,
    Error
}

public static class TrayStateResolver
{
    public static TrayVisualState Resolve(bool hostOnline, IEnumerable<TaskSummaryDto> tasks)
    {
        if (!hostOnline)
        {
            return TrayVisualState.Error;
        }

        var snapshot = tasks.ToArray();
        if (snapshot.Any(task => task.Status == "WaitingForUser"))
        {
            return TrayVisualState.WaitingForUser;
        }

        if (snapshot.Any(task => task.Status is "Pending" or "Running" or "CancellationRequested"))
        {
            return TrayVisualState.Working;
        }

        return TrayVisualState.Idle;
    }
}
