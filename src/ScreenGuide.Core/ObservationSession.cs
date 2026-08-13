namespace ScreenGuide.Core;

public enum ObservationStatus
{
    Idle,
    Selecting,
    Observing,
    Stopped
}

public sealed class ObservationSession
{
    private ObservationStatus _statusBeforeSelection = ObservationStatus.Idle;
    private string? _targetBeforeSelection;

    public ObservationStatus Status { get; private set; } = ObservationStatus.Idle;

    public string? TargetWindowTitle { get; private set; }

    public bool CanCapture => Status == ObservationStatus.Observing;

    public void BeginSelection()
    {
        if (Status == ObservationStatus.Selecting)
        {
            throw new InvalidOperationException("窗口选择已经开始。");
        }

        _statusBeforeSelection = Status;
        _targetBeforeSelection = TargetWindowTitle;
        Status = ObservationStatus.Selecting;
    }

    public void CompleteSelection(string targetWindowTitle)
    {
        EnsureSelectionInProgress();

        if (string.IsNullOrWhiteSpace(targetWindowTitle))
        {
            throw new ArgumentException("必须由用户明确选择一个窗口。", nameof(targetWindowTitle));
        }

        TargetWindowTitle = targetWindowTitle.Trim();
        Status = ObservationStatus.Observing;
        ClearPreviousSelectionSnapshot();
    }

    public void CancelSelection()
    {
        EnsureSelectionInProgress();

        Status = _statusBeforeSelection;
        TargetWindowTitle = _targetBeforeSelection;
        ClearPreviousSelectionSnapshot();
    }

    public void Stop()
    {
        Status = ObservationStatus.Stopped;
        TargetWindowTitle = null;
        ClearPreviousSelectionSnapshot();
    }

    private void EnsureSelectionInProgress()
    {
        if (Status != ObservationStatus.Selecting)
        {
            throw new InvalidOperationException("当前没有正在进行的窗口选择。");
        }
    }

    private void ClearPreviousSelectionSnapshot()
    {
        _statusBeforeSelection = ObservationStatus.Idle;
        _targetBeforeSelection = null;
    }
}
