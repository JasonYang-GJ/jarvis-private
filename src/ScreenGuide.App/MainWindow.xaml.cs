using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenGuide.App.Services;
using ScreenGuide.Core;

namespace ScreenGuide.App;

public partial class MainWindow : Window
{
    private static readonly SolidColorBrush IdleBrush = new(Color.FromRgb(169, 178, 194));
    private static readonly SolidColorBrush SelectingBrush = new(Color.FromRgb(43, 102, 217));
    private static readonly SolidColorBrush ActiveBrush = new(Color.FromRgb(22, 138, 103));

    private readonly ObservationSession _observationSession = new();
    private readonly WindowsWindowPickerService _windowPicker = new();

    public MainWindow()
    {
        InitializeComponent();
        _windowPicker.SelectedTargetClosed += WindowPicker_SelectedTargetClosed;
    }

    private async void SelectWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_windowPicker.IsSupported)
        {
            ShowUnsupportedState();
            return;
        }

        _observationSession.BeginSelection();
        ShowSelectingState();

        try
        {
            var ownerWindowHandle = new WindowInteropHelper(this).EnsureHandle();
            var selection = await _windowPicker.PickWindowAsync(ownerWindowHandle);

            if (selection is null)
            {
                _observationSession.CancelSelection();
                RestoreCurrentState("你取消了这次选择，没有新增任何授权。");
                return;
            }

            var displayName = string.IsNullOrWhiteSpace(selection.DisplayName)
                ? "已选择的窗口"
                : selection.DisplayName;

            _observationSession.CompleteSelection(displayName);
            ShowSelectedState(selection with { DisplayName = displayName });
        }
        catch (UnauthorizedAccessException)
        {
            _observationSession.CancelSelection();
            RestoreCurrentState("Windows 没有授予窗口选择权限，请重新选择。", showError: true);
        }
        catch (Exception)
        {
            _observationSession.CancelSelection();
            RestoreCurrentState("系统窗口选择器没有正常打开，请稍后重试。", showError: true);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _windowPicker.ClearSelection();
        _observationSession.Stop();
        ShowIdleState("窗口授权已经清除，当前没有读取任何画面。");
    }

    private void WindowPicker_SelectedTargetClosed(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _observationSession.Stop();
            ShowIdleState("你选择的窗口已经关闭，授权已自动清除。");
        });
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _windowPicker.SelectedTargetClosed -= WindowPicker_SelectedTargetClosed;
        _windowPicker.Dispose();
        _observationSession.Stop();
    }

    private void ShowSelectingState()
    {
        StatusLight.Fill = SelectingBrush;
        StatusTitle.Text = "等待你选择";
        StatusDescription.Text = "请在 Windows 系统界面里选择一个软件窗口；点击取消不会授权。";
        SelectionDetailText.Text = "系统选择器打开期间，本软件不会读取画面。";
        NextStepTitle.Text = "在系统界面中选择窗口";
        GuidanceText.Text = "请选择一个具体的软件窗口，例如 GitHub Desktop。不要选择整个桌面或显示器。";
        GuidanceReasonText.Text = "  选择由 Windows 系统完成，本软件不能替你确认。";
        SelectWindowButton.Content = "等待选择…";
        SelectWindowButton.IsEnabled = false;
    }

    private void ShowSelectedState(WindowSelectionResult selection)
    {
        StatusLight.Fill = ActiveBrush;
        StatusTitle.Text = $"已选择：{selection.DisplayName}";
        StatusDescription.Text = "Windows 已返回这个目标。当前版本只保存选择结果，尚未读取画面。";
        SelectionDetailText.Text = $"目标大小：{selection.PixelWidth} × {selection.PixelHeight} 像素";
        NextStepTitle.Text = "窗口授权成功";
        GuidanceText.Text = $"已选择“{selection.DisplayName}”。你现在可以停止观察或重新选择；本步骤没有截取、保存或上传画面。";
        GuidanceReasonText.Text = "  已完成系统选择，下一阶段才会读取一张测试画面。";
        SelectWindowButton.Content = "重新选择软件";
        SelectWindowButton.IsEnabled = true;
        StopButton.IsEnabled = true;
    }

    private void ShowIdleState(string detail)
    {
        StatusLight.Fill = IdleBrush;
        StatusTitle.Text = "尚未选择窗口";
        StatusDescription.Text = "只有你主动选择后，软件才能取得那个窗口的授权。";
        SelectionDetailText.Text = detail;
        NextStepTitle.Text = "先选择一个窗口";
        GuidanceText.Text = "请选择你想学习的软件窗口，例如 GitHub Desktop。第一版会先让你亲自操作，不会接管鼠标。";
        GuidanceReasonText.Text = "  先把观察权限做对，再增加语音和智能判断。";
        SelectWindowButton.Content = "选择要学习的软件";
        SelectWindowButton.IsEnabled = true;
        StopButton.IsEnabled = false;
    }

    private void RestoreCurrentState(string detail, bool showError = false)
    {
        if (_observationSession.CanCapture && _observationSession.TargetWindowTitle is not null)
        {
            StatusLight.Fill = ActiveBrush;
            StatusTitle.Text = $"仍在使用：{_observationSession.TargetWindowTitle}";
            StatusDescription.Text = "原窗口授权保持不变，当前版本尚未读取画面。";
            SelectionDetailText.Text = detail;
            NextStepTitle.Text = "原窗口仍然有效";
            GuidanceText.Text = "你可以继续保留原窗口、重新选择，或者点击“停止观察”清除授权。";
            GuidanceReasonText.Text = "  取消重新选择不会破坏原来的授权。";
            SelectWindowButton.Content = "重新选择软件";
            SelectWindowButton.IsEnabled = true;
            StopButton.IsEnabled = true;
        }
        else
        {
            ShowIdleState(detail);
        }

        if (showError)
        {
            MessageBox.Show(detail, "无法选择窗口", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowUnsupportedState()
    {
        ShowIdleState("这台 Windows 设备不支持系统窗口选择器，未获得任何授权。");
        MessageBox.Show(
            "当前 Windows 设备不支持系统窗口选择器。软件没有读取或上传任何屏幕内容。",
            "设备不支持",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
