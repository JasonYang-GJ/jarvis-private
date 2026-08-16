using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ScreenGuide.App.Controls;

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
    private int _hideRequestVersion;
    private string _lastTitle = "贾维斯已就绪";
    private string _lastDetail = "右键可以启动语音、缩小或打开设置";
    private System.Windows.Media.Color _lastAccent = System.Windows.Media.Color.FromRgb(72, 231, 255);
    private ParticleHelmetState _lastVisualState = ParticleHelmetState.Offline;

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
            _hideRequestVersion++;
            LargeParticleHelmet.CancelDissolve();
            CompactParticleHelmet.CancelDissolve();
            SetCompactMode(!expanded && _isCompact);
            if (!IsVisible)
            {
                Show();
            }
            if (_isCompact)
            {
                CompactParticleHelmet.TriggerAssembly(0.65);
            }
            else
            {
                LargeParticleHelmet.TriggerAssembly(2.2);
            }
            Activate();
        });
    }

    public void HideToTray()
    {
        Dispatcher.Invoke(() =>
        {
            _userHidden = true;
            var requestVersion = ++_hideRequestVersion;
            LargeParticleHelmet.PlayDissolve();
            CompactParticleHelmet.PlayDissolve();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_userHidden && requestVersion == _hideRequestVersion)
                {
                    Hide();
                }
            };
            timer.Start();
        });
    }

    public void ShowReady() => ShowWaitingForWakeWord();

    public void ShowWaitingForWakeWord()
    {
        SetVoiceActive(true);
        ShowStatus(
            "贾维斯正在待命",
            $"说“你好贾维斯”；备用 {_voiceShortcut}；{_stopShortcut} 停止",
            System.Windows.Media.Color.FromRgb(72, 231, 255),
            ParticleHelmetState.Waiting);
    }

    public void ShowListening()
    {
        ShowStatus(
            "我在，请直接说",
            "说完后停顿一下，我会自动开始处理",
            System.Windows.Media.Color.FromRgb(72, 231, 255),
            ParticleHelmetState.Listening);
    }

    public void ShowContinuousConversation()
    {
        ShowStatus(
            "连续对话中",
            "直接说下一句；说“你退下吧”或“退出”即可结束",
            System.Windows.Media.Color.FromRgb(72, 231, 255),
            ParticleHelmetState.Waiting);
    }

    public void ShowRecognized(string text)
    {
        ShowStatus(
            "已经听清",
            text,
            System.Windows.Media.Color.FromRgb(205, 232, 244),
            ParticleHelmetState.Recognized);
    }

    public void ShowProcessing(string title, string detail)
    {
        ShowStatus(
            title,
            string.IsNullOrWhiteSpace(detail) ? "请稍候…" : detail,
            System.Windows.Media.Color.FromRgb(255, 112, 69),
            ParticleHelmetState.Thinking);
    }

    public void ShowAnswer(string answer)
    {
        var normalized = answer.ReplaceLineEndings(" ").Trim();
        var preview = normalized.Length <= 120 ? normalized : normalized[..120] + "…";
        ShowStatus(
            "贾维斯正在回答",
            preview,
            System.Windows.Media.Color.FromRgb(72, 231, 255),
            ParticleHelmetState.Speaking);
    }

    public void ShowProblem(string message)
    {
        ShowStatus(
            "这次没有完成",
            message,
            System.Windows.Media.Color.FromRgb(233, 48, 69),
            ParticleHelmetState.Alert);
    }

    public void ShowStopped()
    {
        SetVoiceActive(false);
        ShowStatus(
            "语音尚未启动",
            "右键选择“启动语音陪伴”；设置和退出也都在右键菜单中",
            System.Windows.Media.Color.FromRgb(104, 155, 177),
            ParticleHelmetState.Offline);
    }

    private void ShowStatus(
        string title,
        string detail,
        System.Windows.Media.Color accent,
        ParticleHelmetState visualState)
    {
        Dispatcher.Invoke(() =>
        {
            _lastTitle = title;
            _lastDetail = detail;
            _lastAccent = accent;
            _lastVisualState = visualState;
            ApplyStatus();
            RevealStatusTemporarily();

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
        AvatarRoot.ToolTip = $"{_lastTitle}\n{_lastDetail}";
        var accentBrush = new SolidColorBrush(_lastAccent);
        StateAccentBorder.Background = accentBrush;
        CompactStateAccent.Background = accentBrush;
        StatusPill.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(105, _lastAccent.R, _lastAccent.G, _lastAccent.B));
        LargeParticleHelmet.SetState(_lastVisualState);
        CompactParticleHelmet.SetState(_lastVisualState);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        extendedStyle |= WsExToolWindow;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(extendedStyle));

        SetCompactMode(false);
        ApplyStatus();
        LargeParticleHelmet.TriggerAssembly(2.2);
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        HoverControls.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        StatusPill.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        HoverControls.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)));
        StatusPill.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)));
    }

    private void RevealStatusTemporarily()
    {
        var animation = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(450),
            BeginTime = TimeSpan.FromSeconds(2.4),
            FillBehavior = FillBehavior.HoldEnd
        };
        StatusPill.BeginAnimation(OpacityProperty, animation);
    }

    private void SetCompactMode(bool compact)
    {
        _isCompact = compact;
        if (compact)
        {
            LargeAvatarView.Visibility = Visibility.Collapsed;
            CompactAvatarView.Visibility = Visibility.Visible;
            Width = 122;
            Height = 122;
            SizeMenuItem.Header = "展开贾维斯";
            PositionCompact();
            CompactParticleHelmet.TriggerAssembly(0.62);
        }
        else
        {
            CompactAvatarView.Visibility = Visibility.Collapsed;
            LargeAvatarView.Visibility = Visibility.Visible;
            Width = 430;
            Height = 455;
            SizeMenuItem.Header = "缩小贾维斯";
            PositionLarge();
            LargeParticleHelmet.TriggerAssembly(1.35);
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
