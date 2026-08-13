namespace ScreenGuide.Core;

public enum ObservationStatus
{
    Idle,
    Observing,
    Stopped
}

public sealed class ObservationSession
{
    public ObservationStatus Status { get; private set; } = ObservationStatus.Idle;

    public string? TargetWindowTitle { get; private set; }

    public bool CanCapture => Status == ObservationStatus.Observing;

    public void Start(string targetWindowTitle)
    {
        if (string.IsNullOrWhiteSpace(targetWindowTitle))
        {
            throw new ArgumentException("必须由用户明确选择一个窗口。", nameof(targetWindowTitle));
        }

        TargetWindowTitle = targetWindowTitle.Trim();
        Status = ObservationStatus.Observing;
    }

    public void Stop()
    {
        Status = ObservationStatus.Stopped;
        TargetWindowTitle = null;
    }
}
