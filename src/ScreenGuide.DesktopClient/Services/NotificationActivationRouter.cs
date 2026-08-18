namespace ScreenGuide.DesktopClient.Services;

internal sealed class NotificationActivationRouter
{
    private Guid? _taskId;

    public void MarkShown(Guid taskId) => _taskId = taskId;

    public bool TryActivate(out Guid taskId)
    {
        if (_taskId is not { } value)
        {
            taskId = default;
            return false;
        }

        taskId = value;
        return true;
    }
}
