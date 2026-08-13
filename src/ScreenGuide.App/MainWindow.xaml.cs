using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using ScreenGuide.App.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ScreenGuide.App;

public partial class MainWindow : Window
{
    private static readonly SolidColorBrush IdleBrush = new(System.Windows.Media.Color.FromRgb(169, 178, 194));
    private static readonly SolidColorBrush ActiveBrush = new(System.Windows.Media.Color.FromRgb(22, 138, 103));
    private static readonly SolidColorBrush ListeningBrush = new(System.Windows.Media.Color.FromRgb(43, 102, 217));

    private readonly GlobalHotkeyService _hotkeys = new();
    private readonly WindowsSpeechService _speech = new();
    private readonly OverlayWindow _overlay = new();

    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private SpeechCapabilityReport? _speechCapabilities;
    private CancellationTokenSource? _voiceCancellation;
    private bool _backgroundEnabled;
    private bool _voiceRequestInProgress;
    private bool _hotkeysReady;
    private bool _allowExit;
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        InitializeTrayIcon();

        try
        {
            _hotkeys.Register(this);
            _hotkeys.VoiceRequested += Hotkeys_VoiceRequested;
            _hotkeys.StopRequested += Hotkeys_StopRequested;
            VoiceShortcutText.Text = _hotkeys.VoiceShortcutDisplay;
            _overlay.ConfigureShortcuts(_hotkeys.VoiceShortcutDisplay, _hotkeys.StopShortcutDisplay);
            _hotkeysReady = true;
        }
        catch (Win32Exception exception)
        {
            StartBackgroundButton.IsEnabled = false;
            _hotkeysReady = false;
            StatusTitle.Text = "快捷键无法使用";
            StatusDescription.Text = exception.Message;
            VoiceShortcutText.Text = "没有找到可用快捷键";
        }

        RefreshSpeechCapabilities();
    }

    private async void StartBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        if (_speechCapabilities is null)
        {
            RefreshSpeechCapabilities();
        }

        if (!_hotkeysReady || _speechCapabilities is not { IsReady: true })
        {
            System.Windows.MessageBox.Show(
                "这台电脑还缺少中文语音识别或中文语音播报组件。请先按照右侧检查结果安装，然后重新打开本程序。",
                "语音组件尚未准备好",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _backgroundEnabled = true;
        StartBackgroundButton.IsEnabled = false;
        StopBackgroundButton.IsEnabled = true;
        StatusLight.Fill = ActiveBrush;
        StatusTitle.Text = "后台陪练已启动";
        StatusDescription.Text = $"可以停留在任何软件里，按 {_hotkeys.VoiceShortcutDisplay} 说话。";
        LastVoiceStatusText.Text = "尚未使用麦克风。";

        Hide();
        _overlay.ShowReady();
        ShowTrayMessage(
            "后台陪练已启动",
            $"按 {_hotkeys.VoiceShortcutDisplay} 开始说话。按 {_hotkeys.StopShortcutDisplay} 停止。");

        try
        {
            await _speech.SpeakChineseAsync(
                "后台陪练已启动。请按屏幕提示的语音快捷键，然后直接说话。",
                CancellationToken.None);
        }
        catch
        {
            _overlay.ShowProblem("语音播报没有启动，请从右下角图标打开设置检查。 ");
        }
    }

    private void StopBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        StopBackgroundMode(showSettings: true);
    }

    private void Hotkeys_VoiceRequested(object? sender, EventArgs e)
    {
        if (!_backgroundEnabled || _voiceRequestInProgress)
        {
            return;
        }

        _ = RunVoiceRequestAsync();
    }

    private void Hotkeys_StopRequested(object? sender, EventArgs e)
    {
        if (_backgroundEnabled)
        {
            StopBackgroundMode(showSettings: false);
        }
    }

    private async Task RunVoiceRequestAsync()
    {
        _voiceRequestInProgress = true;
        _voiceCancellation = new CancellationTokenSource();
        var cancellationToken = _voiceCancellation.Token;

        StatusLight.Fill = ListeningBrush;
        StatusTitle.Text = "正在听";
        StatusDescription.Text = "说完后停顿一下，本次监听会自动结束。";
        LastVoiceStatusText.Text = "麦克风只在这一次提问期间开启。";
        _overlay.ShowListening();

        try
        {
            var recognizedText = await _speech.RecognizeChineseOnceAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(recognizedText))
            {
                _overlay.ShowProblem("没有识别到清楚的话，请再按一次快捷键重试。 ");
                LastVoiceStatusText.Text = "这次没有识别到文字。";
                await _speech.SpeakChineseAsync("这次没有听清，请再说一次。", cancellationToken);
                return;
            }

            _overlay.ShowRecognized($"你说：{recognizedText}");
            LastVoiceStatusText.Text = $"刚刚听到：{recognizedText}";
            StatusLight.Fill = ActiveBrush;
            StatusTitle.Text = "已经听清";
            StatusDescription.Text = "语音通道正常；下一步接入当前窗口画面和 AI 回答。";

            var spokenReply = $"我听到了。你说的是，{recognizedText}。语音陪练通道已经工作。";
            await _speech.SpeakChineseAsync(spokenReply, cancellationToken);

            await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
            if (_backgroundEnabled)
            {
                _overlay.ShowReady();
            }
        }
        catch (OperationCanceledException)
        {
            // StopBackgroundMode already updates the visible state.
        }
        catch (InvalidOperationException)
        {
            ShowVoiceProblem("麦克风或中文语音识别不可用。请打开 Windows 设置检查麦克风权限和中文语音组件。");
        }
        catch
        {
            ShowVoiceProblem("语音识别没有正常完成。请确认麦克风已连接，然后再按一次快捷键。");
        }
        finally
        {
            _voiceCancellation?.Dispose();
            _voiceCancellation = null;
            _voiceRequestInProgress = false;

            if (_backgroundEnabled && StatusTitle.Text == "正在听")
            {
                StatusLight.Fill = ActiveBrush;
                StatusTitle.Text = "后台陪练已启动";
                StatusDescription.Text = $"按 {_hotkeys.VoiceShortcutDisplay} 可以重新说话。";
            }
        }
    }

    private void ShowVoiceProblem(string message)
    {
        _overlay.ShowProblem(message);
        StatusLight.Fill = IdleBrush;
        StatusTitle.Text = "语音功能需要检查";
        StatusDescription.Text = message;
        LastVoiceStatusText.Text = message;
    }

    private void StopBackgroundMode(bool showSettings)
    {
        _backgroundEnabled = false;
        _voiceCancellation?.Cancel();
        StartBackgroundButton.IsEnabled = _hotkeysReady && _speechCapabilities is { IsReady: true };
        StopBackgroundButton.IsEnabled = false;
        StatusLight.Fill = IdleBrush;
        StatusTitle.Text = "后台陪练已停止";
        StatusDescription.Text = "当前没有监听麦克风，也不会在后台响应语音快捷键。";
        LastVoiceStatusText.Text = "麦克风已停止。";
        _overlay.ShowStopped();

        if (showSettings)
        {
            OpenSettings();
        }
        else
        {
            _ = HideOverlayAfterDelayAsync();
            ShowTrayMessage("后台陪练已停止", "从右下角图标可以重新打开设置。");
        }
    }

    private async Task HideOverlayAfterDelayAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        if (!_backgroundEnabled)
        {
            _overlay.Hide();
        }
    }

    private void RefreshSpeechCapabilities()
    {
        _speechCapabilities = _speech.GetCapabilityReport();

        RecognitionCapabilityText.Text = _speechCapabilities.HasChineseRecognizer
            ? $"✓ 中文语音识别：{_speechCapabilities.RecognizerName}"
            : "✕ 未检测到中文语音识别。请在 Windows 设置的语言选项中安装“语音”。";
        RecognitionCapabilityText.Foreground = _speechCapabilities.HasChineseRecognizer
            ? ActiveBrush
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 79, 20));

        VoiceCapabilityText.Text = _speechCapabilities.HasChineseVoice
            ? $"✓ 中文语音播报：{_speechCapabilities.VoiceName}"
            : "✕ 未检测到中文语音播报。请在 Windows 设置中安装中文语音包。";
        VoiceCapabilityText.Foreground = _speechCapabilities.HasChineseVoice
            ? ActiveBrush
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 79, 20));

        if (_speechCapabilities.IsReady && _hotkeysReady)
        {
            StatusTitle.Text = "语音组件已就绪";
            StatusDescription.Text = "可以启动后台陪练。启动后主窗口会隐藏。";
            StartBackgroundButton.IsEnabled = true;
        }
        else if (!_hotkeysReady)
        {
            StatusTitle.Text = "快捷键无法使用";
            StatusDescription.Text = "常用的语音快捷键都被其他软件占用，请先关闭冲突软件。";
            StartBackgroundButton.IsEnabled = false;
        }
        else
        {
            StatusTitle.Text = "缺少语音组件";
            StatusDescription.Text = "请先根据右侧提示补齐中文语音组件。";
            StartBackgroundButton.IsEnabled = false;
        }
    }

    private void InitializeTrayIcon()
    {
        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("打开设置", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        _trayMenu.Items.Add("退出屏幕陪练老师", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "屏幕陪练老师",
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        _notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(OpenSettings);
    }

    private void OpenSettings()
    {
        if (!IsVisible)
        {
            Show();
        }

        WindowState = WindowState.Normal;
        Activate();
        _overlay.Hide();
    }

    private void ShowTrayMessage(string title, string text)
    {
        _notifyIcon?.ShowBalloonTip(2500, title, text, Forms.ToolTipIcon.Info);
    }

    private void ExitApplication()
    {
        _allowExit = true;
        _backgroundEnabled = false;
        _voiceCancellation?.Cancel();
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_backgroundEnabled && !_allowExit)
        {
            e.Cancel = true;
            Hide();
            _overlay.ShowReady();
            ShowTrayMessage(
                "屏幕陪练仍在后台",
                $"按 {_hotkeys.VoiceShortcutDisplay} 说话，或从右下角图标退出。");
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _hotkeys.VoiceRequested -= Hotkeys_VoiceRequested;
        _hotkeys.StopRequested -= Hotkeys_StopRequested;
        _hotkeys.Dispose();
        _voiceCancellation?.Cancel();
        _voiceCancellation?.Dispose();
        _notifyIcon?.Dispose();
        _trayMenu?.Dispose();

        try
        {
            _overlay.Close();
        }
        catch (InvalidOperationException)
        {
            // The overlay was already closed during application shutdown.
        }
    }
}
