using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace ScreenGuide.App;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private string _voiceShortcut = "Ctrl + Shift + 空格";
    private string _stopShortcut = "Ctrl + Shift + Q";
    private bool _isCompact;
    private bool _userHidden;
    private bool _voiceActive;
    private string _lastTitle = "贾维斯已就绪";
    private string _lastDetail = "右键可以启动语音、缩小或打开设置";
    private System.Windows.Media.Color _lastAccent = System.Windows.Media.Color.FromRgb(66, 216, 255);

    public event EventHandler? SettingsRequested;
    public event EventHandler? StartVoiceRequested;
    public event EventHandler? StopVoiceRequested;
    public event EventHandler? ExitRequested;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    public void ConfigureShortcuts(string voiceShortcut, string stopShortcut)
    {
        _voiceShortcut = voiceShortcut;
        _stopShortcut = stopShortcut;
    }

    public void SetAvatarImage(BitmapSource image)
    {
        LargeAvatarBrush.ImageSource = image;
        CompactAvatarBrush.ImageSource = image;
    }

    public void SetVoiceActive(bool active)
    {
        _voiceActive = active;
        StartVoiceMenuItem.IsEnabled = !active;
        StopVoiceMenuItem.IsEnabled = active;
    }

    public void ShowAvatar(bool expanded = false)
    {
        Dispatcher.Invoke(() =>
        {
            _userHidden = false;
            SetCompactMode(!expanded && _isCompact);
            if (!IsVisible)
            {
                Show();
            }
            Activate();
        });
    }

    public void HideToTray()
    {
        _userHidden = true;
        Hide();
    }

    public void ShowReady() => ShowWaitingForWakeWord();

    public void ShowWaitingForWakeWord()
    {
        SetVoiceActive(true);
        ShowStatus(
            "贾维斯正在待命",
            $"说“你好贾维斯”；备用 {_voiceShortcut}；{_stopShortcut} 停止",
            System.Windows.Media.Color.FromRgb(66, 216, 255));
    }

    public void ShowListening()
    {
        ShowStatus(
            "我在，请直接说",
            "说完后停顿一下，系统会自动结束本次录音",
            System.Windows.Media.Color.FromRgb(69, 229, 255));
    }

    public void ShowRecognized(string text)
    {
        ShowStatus(
            "已经听清",
            text,
            System.Windows.Media.Color.FromRgb(52, 225, 181));
    }

    public void ShowProcessing(string title, string detail)
    {
        ShowStatus(
            title,
            string.IsNullOrWhiteSpace(detail) ? "请稍候…" : detail,
            System.Windows.Media.Color.FromRgb(255, 84, 92));
    }

    public void ShowAnswer(string answer)
    {
        var normalized = answer.ReplaceLineEndings(" ").Trim();
        var preview = normalized.Length <= 120 ? normalized : normalized[..120] + "…";
        ShowStatus(
            "贾维斯正在回答",
            preview,
            System.Windows.Media.Color.FromRgb(66, 216, 255));
    }

    public void ShowProblem(string message)
    {
        ShowStatus(
            "这次没有完成",
            message,
            System.Windows.Media.Color.FromRgb(255, 67, 79));
    }

    public void ShowStopped()
    {
        SetVoiceActive(false);
        ShowStatus(
            "语音尚未启动",
            "右键选择“启动语音陪伴”；设置和退出也都在右键菜单中",
            System.Windows.Media.Color.FromRgb(126, 149, 166));
    }

    private void ShowStatus(string title, string detail, System.Windows.Media.Color accent)
    {
        Dispatcher.Invoke(() =>
        {
            _lastTitle = title;
            _lastDetail = detail;
            _lastAccent = accent;
            ApplyStatus();

            if (!_userHidden && !IsVisible)
            {
                Show();
            }
        });
    }

    private void ApplyStatus()
    {
        OverlayTitle.Text = _lastTitle;
        OverlayDetail.Text = _lastDetail;
        var accentBrush = new SolidColorBrush(_lastAccent);
        StatusDot.Fill = accentBrush;
        CompactStatusDot.Fill = accentBrush;
        StateHalo.Stroke = accentBrush;
        CompactHalo.Stroke = accentBrush;
        HaloShadow.Color = _lastAccent;
        CompactHaloShadow.Color = _lastAccent;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        extendedStyle |= WsExToolWindow;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(extendedStyle));

        StartAmbientAnimation();
        SetCompactMode(false);
        ApplyStatus();
    }

    private void StartAmbientAnimation()
    {
        OuterRingRotation.BeginAnimation(
            System.Windows.Media.RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(24))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
        InnerRingRotation.BeginAnimation(
            System.Windows.Media.RotateTransform.AngleProperty,
            new DoubleAnimation(360, 0, TimeSpan.FromSeconds(18))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });

        var pulse = new DoubleAnimation(0.22, 0.5, TimeSpan.FromSeconds(1.4))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        StateHalo.BeginAnimation(OpacityProperty, pulse);
        CompactHalo.BeginAnimation(OpacityProperty, pulse);
    }

    private void SetCompactMode(bool compact)
    {
        _isCompact = compact;
        if (compact)
        {
            LargeAvatarView.Visibility = Visibility.Collapsed;
            CompactAvatarView.Visibility = Visibility.Visible;
            Width = 118;
            Height = 118;
            SizeMenuItem.Header = "展开贾维斯";
            PositionCompact();
        }
        else
        {
            CompactAvatarView.Visibility = Visibility.Collapsed;
            LargeAvatarView.Visibility = Visibility.Visible;
            Width = 410;
            Height = 500;
            SizeMenuItem.Header = "缩小贾维斯";
            PositionLarge();
        }
    }

    private void PositionLarge()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;
    }

    private void PositionCompact()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 22;
        Top = workArea.Top + 32;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            SetCompactMode(!_isCompact);
            e.Handled = true;
            return;
        }

        if (FindParent<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject) is null)
        {
            DragMove();
        }
    }

    private static T? FindParent<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void CompactButton_Click(object sender, RoutedEventArgs e) => SetCompactMode(true);
    private void HideButton_Click(object sender, RoutedEventArgs e) => HideToTray();
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void SizeMenuItem_Click(object sender, RoutedEventArgs e) => SetCompactMode(!_isCompact);
    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void HideMenuItem_Click(object sender, RoutedEventArgs e) => HideToTray();
    private void StartVoiceMenuItem_Click(object sender, RoutedEventArgs e) => StartVoiceRequested?.Invoke(this, EventArgs.Empty);
    private void StopVoiceMenuItem_Click(object sender, RoutedEventArgs e) => StopVoiceRequested?.Invoke(this, EventArgs.Empty);
    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newLong);
}
