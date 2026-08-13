using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScreenGuide.App;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);
    private string _voiceShortcut = "Ctrl + Shift + 空格";
    private string _stopShortcut = "Ctrl + Shift + Q";

    public OverlayWindow()
    {
        InitializeComponent();
    }

    public void ConfigureShortcuts(string voiceShortcut, string stopShortcut)
    {
        _voiceShortcut = voiceShortcut;
        _stopShortcut = stopShortcut;
    }

    public void ShowReady()
    {
        ShowStatus(
            "后台陪练已就绪",
            $"按 {_voiceShortcut} 开始说话；{_stopShortcut} 停止",
            "●",
            System.Windows.Media.Color.FromRgb(22, 138, 103));
    }

    public void ShowListening()
    {
        ShowStatus(
            "正在听，请直接说话",
            "说完后停顿一下，系统会自动结束本次录音",
            "●",
            System.Windows.Media.Color.FromRgb(43, 102, 217));
    }

    public void ShowRecognized(string text)
    {
        ShowStatus(
            "已经听清",
            text,
            "✓",
            System.Windows.Media.Color.FromRgb(22, 138, 103));
    }

    public void ShowProblem(string message)
    {
        ShowStatus(
            "这次没有听清",
            message,
            "!",
            System.Windows.Media.Color.FromRgb(201, 111, 24));
    }

    public void ShowStopped()
    {
        ShowStatus(
            "后台陪练已停止",
            "可以从任务栏右下角图标重新打开设置",
            "■",
            System.Windows.Media.Color.FromRgb(101, 112, 135));
    }

    private void ShowStatus(string title, string detail, string symbol, System.Windows.Media.Color accent)
    {
        Dispatcher.Invoke(() =>
        {
            OverlayTitle.Text = title;
            OverlayDetail.Text = detail;
            StatusSymbol.Text = symbol;
            StatusSymbol.Foreground = new SolidColorBrush(accent);
            StatusPulse.Fill = new SolidColorBrush(accent);

            PositionAtTopRight();
            if (!IsVisible)
            {
                Show();
            }

            KeepTopmostWithoutActivation();
        });
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        extendedStyle |= WsExNoActivate | WsExToolWindow | WsExTransparent;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(extendedStyle));
        PositionAtTopRight();
        KeepTopmostWithoutActivation();
    }

    private void PositionAtTopRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 22;
        Top = workArea.Top + 22;
    }

    private void KeepTopmostWithoutActivation()
    {
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(
            handle,
            HwndTopmost,
            (int)Left,
            (int)Top,
            (int)Width,
            (int)Height,
            SwpNoActivate | SwpShowWindow);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
