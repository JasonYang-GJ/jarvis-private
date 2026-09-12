using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ScreenGuide.DesktopClient.Services;

namespace ScreenGuide.DesktopClient;

public partial class MainWindow
{
    private readonly PointerGestureGate _pointerGesture = new(TimeProvider.System);
    private TemporaryPointerHotkey? _pointerHotkey;
    private DispatcherTimer? _pointerGestureTimer;
    private Func<bool>? _pointerGestureStillValid;
    private readonly PointerRequestCancellation _pointerRequestCancellation = new();
    private Task _pointerCancellationCompletion = Task.CompletedTask;

    private void ArmPointerGesture(DateTimeOffset expiresAt, string instruction,
        Func<bool> stillValid, Func<CancellationToken, Task> action)
    {
        CancelPendingPointerGesture(cancelRequest: false);
        try
        {
            _pointerHotkey = new TemporaryPointerHotkey(new WindowInteropHelper(this).Handle, OnPointerGesturePressed);
            _pointerGesture.Arm(expiresAt, action, _lifetime.Token);
            _pointerGestureStillValid = stillValid;
            PointerGestureInstruction.Text = instruction;
            PointerGesturePanel.Visibility = Visibility.Visible;
            _pointerGestureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _pointerGestureTimer.Tick += (_, _) =>
            {
                if (!_pointerGesture.Expire() && stillValid()) return;
                CancelPendingPointerGesture();
                StatusBarText.Text = "这次指针操作已过期或上下文已变化，请重新提问。";
                _ = InvalidateDisplayedPointerConsentAsync();
            };
            _pointerGestureTimer.Start();
        }
        catch { CancelPendingPointerGesture(); throw; }
    }

    private async void OnPointerGesturePressed()
    {
        if (_pointerGestureStillValid?.Invoke() != true) { CancelPendingPointerGesture(); return; }
        if (TemporaryPointerHotkey.GetForegroundWindow() == new WindowInteropHelper(this).Handle)
        {
            StatusBarText.Text = "请先切到目标应用窗口，再按 Ctrl+Alt+F8。";
            return;
        }
        ClearPointerGestureVisuals();
        try { await _pointerGesture.ExecuteAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ShowError("指针操作未完成，请检查目标窗口后重新提问。", exception); }
    }

    private void ClearPointerGestureVisuals()
    {
        _pointerHotkey?.Dispose();
        _pointerHotkey = null;
        _pointerGestureTimer?.Stop();
        _pointerGestureTimer = null;
        _pointerGestureStillValid = null;
        if (PointerGesturePanel is not null) PointerGesturePanel.Visibility = Visibility.Collapsed;
    }

    private void CancelPendingPointerGesture(bool cancelRequest = true)
    {
        ClearPointerGestureVisuals();
        _pointerGesture.Cancel();
        if (cancelRequest)
        {
            _pointerCancellationCompletion = _pointerRequestCancellation.CancelAsync();
            _ = ObservePointerCancellationAsync(_pointerCancellationCompletion);
        }
    }

    private async Task ObservePointerCancellationAsync(Task cancellation)
    {
        try { await cancellation; }
        catch (Exception exception) { ShowError("尚未确认后台请求已停止，请重试停止。", exception); }
    }

    private async void CancelPointerGestureButton_Click(object sender, RoutedEventArgs e)
    {
        CancelPendingPointerGesture();
        try { await _pointerCancellationCompletion; }
        catch { return; }
        StatusBarText.Text = "已取消这次指针操作。";
    }
}
