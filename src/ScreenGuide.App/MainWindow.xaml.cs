using System.ComponentModel;
using System.Media;
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
    private readonly SherpaVoiceAssistantService _voiceAssistant = new(LocalVoiceModelPaths.Create());

    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _stopListeningMenuItem;
    private SpeechCapabilityReport? _speechCapabilities;
    private CancellationTokenSource? _backgroundCancellation;
    private bool _backgroundEnabled;
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

        _voiceAssistant.WakeWordDetected += VoiceAssistant_WakeWordDetected;
        _voiceAssistant.QuestionRecognized += VoiceAssistant_QuestionRecognized;
        _voiceAssistant.StatusChanged += VoiceAssistant_StatusChanged;
        _voiceAssistant.Failed += VoiceAssistant_Failed;

        try
        {
            _hotkeys.Register(this);
            _hotkeys.VoiceRequested += Hotkeys_VoiceRequested;
            _hotkeys.StopRequested += Hotkeys_StopRequested;
            VoiceShortcutText.Text = $"备用：{_hotkeys.VoiceShortcutDisplay}";
            _overlay.ConfigureShortcuts(_hotkeys.VoiceShortcutDisplay, _hotkeys.StopShortcutDisplay);
            _hotkeysReady = true;
        }
        catch (Win32Exception)
        {
            _hotkeysReady = false;
            VoiceShortcutText.Text = "备用快捷键被占用，不影响语音唤醒";
            _overlay.ConfigureShortcuts("语音说“你好贾维斯”", "托盘菜单停止");
        }

        RefreshSpeechCapabilities();
    }

    private async void StartBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        if (_speechCapabilities is null)
        {
            RefreshSpeechCapabilities();
        }

        if (_speechCapabilities is not { IsReady: true })
        {
            System.Windows.MessageBox.Show(
                $"本机语音模型或中文播报尚未准备好。模型目录：{LocalVoiceModelPaths.ModelRoot}",
                "本地语音尚未准备好",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        StartBackgroundButton.IsEnabled = false;
        StatusLight.Fill = ListeningBrush;
        StatusTitle.Text = "正在加载本地语音模型";
        StatusDescription.Text = "第一次启动通常需要 10—20 秒，请稍候。";
        LastVoiceStatusText.Text = "音频只在内存中处理，不保存、不上传。";

        _backgroundCancellation?.Dispose();
        _backgroundCancellation = new CancellationTokenSource();

        try
        {
            await _voiceAssistant.StartAsync(_backgroundCancellation.Token);
        }
        catch (Exception exception)
        {
            _backgroundCancellation.Dispose();
            _backgroundCancellation = null;
            ShowVoiceProblem($"本地语音没有启动：{exception.Message}");
            StartBackgroundButton.IsEnabled = true;
            return;
        }

        _backgroundEnabled = true;
        StopBackgroundButton.IsEnabled = true;
        if (_stopListeningMenuItem is not null)
        {
            _stopListeningMenuItem.Enabled = true;
        }
        if (_notifyIcon is not null)
        {
            _notifyIcon.Text = "屏幕陪练老师（正在本机等待唤醒）";
        }

        StatusLight.Fill = ActiveBrush;
        StatusTitle.Text = "正在等待语音唤醒";
        StatusDescription.Text = "留在任何软件里，直接说“你好贾维斯”。";
        LastVoiceStatusText.Text = "麦克风正在本机监听唤醒词；没有保存录音。";

        Hide();
        _overlay.ShowWaitingForWakeWord();
        ShowTrayMessage(
            "贾维斯语音陪练已启动",
            $"直接说“你好贾维斯”。{(_hotkeysReady ? $"{_hotkeys.StopShortcutDisplay} 可随时停止。" : "可从托盘菜单随时停止。")}");

        try
        {
            await _speech.SpeakChineseAsync("后台语音陪练已启动。", _backgroundCancellation.Token);
        }
        catch
        {
            // The tray and overlay still expose the active state.
        }
    }

    private async void StopBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        await StopBackgroundModeAsync(showSettings: true);
    }

    private void Hotkeys_VoiceRequested(object? sender, EventArgs e)
    {
        if (_backgroundEnabled)
        {
            _voiceAssistant.BeginQuestionListening();
        }
    }

    private async void Hotkeys_StopRequested(object? sender, EventArgs e)
    {
        if (_backgroundEnabled)
        {
            await StopBackgroundModeAsync(showSettings: false);
        }
    }

    private void VoiceAssistant_WakeWordDetected(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            SystemSounds.Asterisk.Play();
            StatusLight.Fill = ListeningBrush;
            StatusTitle.Text = "我在，请说";
            StatusDescription.Text = "说完后停顿一下；识别会自动结束。";
            LastVoiceStatusText.Text = "已听到唤醒词，正在识别这一句话。";
            _overlay.ShowListening();
        });
    }

    private void VoiceAssistant_QuestionRecognized(object? sender, VoiceQuestionEventArgs e)
    {
        Dispatcher.BeginInvoke(async () => await HandleRecognizedQuestionAsync(e.Text));
    }

    private async Task HandleRecognizedQuestionAsync(string recognizedText)
    {
        if (!_backgroundEnabled || _backgroundCancellation is null)
        {
            return;
        }

        _overlay.ShowRecognized($"你说：{recognizedText}");
        LastVoiceStatusText.Text = $"刚刚听到：{recognizedText}";
        StatusLight.Fill = ActiveBrush;
        StatusTitle.Text = "已经听清";
        StatusDescription.Text = "回答结束后会自动继续等待唤醒词。";

        try
        {
            var spokenReply = $"我听到了。你说的是，{recognizedText}。目前语音唤醒和识别已经工作，下一步接入画面理解和真正的人工智能回答。";
            await _speech.SpeakChineseAsync(spokenReply, _backgroundCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            _overlay.ShowProblem("已经识别文字，但系统中文语音播报失败。 ");
        }

        if (_backgroundEnabled)
        {
            _voiceAssistant.ResumeWakeWordListening();
            _overlay.ShowWaitingForWakeWord();
        }
    }

    private void VoiceAssistant_StatusChanged(object? sender, VoiceStatusEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_backgroundEnabled)
            {
                return;
            }

            LastVoiceStatusText.Text = e.Message;
            if (e.Message.Contains("没有听清", StringComparison.Ordinal))
            {
                _overlay.ShowProblem("没有听清问题，已继续等待“你好贾维斯”。");
                _ = RestoreWaitingOverlayAfterDelayAsync();
            }
        });
    }

    private async Task RestoreWaitingOverlayAfterDelayAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (_backgroundEnabled)
        {
            _overlay.ShowWaitingForWakeWord();
        }
    }

    private void VoiceAssistant_Failed(object? sender, VoiceStatusEventArgs e)
    {
        Dispatcher.BeginInvoke(() => ShowVoiceProblem(e.Message));
    }

    private void ShowVoiceProblem(string message)
    {
        _overlay.ShowProblem(message);
        StatusLight.Fill = IdleBrush;
        StatusTitle.Text = "语音功能需要检查";
        StatusDescription.Text = message;
        LastVoiceStatusText.Text = message;
    }

    private async Task StopBackgroundModeAsync(bool showSettings)
    {
        _backgroundEnabled = false;
        _backgroundCancellation?.Cancel();
        await _voiceAssistant.StopAsync();
        _backgroundCancellation?.Dispose();
        _backgroundCancellation = null;

        StartBackgroundButton.IsEnabled = _speechCapabilities is { IsReady: true };
        StopBackgroundButton.IsEnabled = false;
        if (_stopListeningMenuItem is not null)
        {
            _stopListeningMenuItem.Enabled = false;
        }
        if (_notifyIcon is not null)
        {
            _notifyIcon.Text = "屏幕陪练老师（麦克风已停止）";
        }

        StatusLight.Fill = IdleBrush;
        StatusTitle.Text = "后台陪练已停止";
        StatusDescription.Text = "当前没有监听麦克风，也不会响应唤醒词。";
        LastVoiceStatusText.Text = "麦克风已停止。";
        _overlay.ShowStopped();

        if (showSettings)
        {
            OpenSettings();
        }
        else
        {
            _ = HideOverlayAfterDelayAsync();
            ShowTrayMessage("后台陪练已停止", "麦克风已经关闭。可从右下角图标重新打开设置。 ");
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
            ? $"✓ 本地唤醒与中文识别：{_speechCapabilities.RecognizerName}"
            : $"✕ 本地语音模型不完整：{LocalVoiceModelPaths.ModelRoot}";
        RecognitionCapabilityText.Foreground = _speechCapabilities.HasChineseRecognizer
            ? ActiveBrush
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 79, 20));

        VoiceCapabilityText.Text = _speechCapabilities.HasChineseVoice
            ? $"✓ 中文语音播报：{_speechCapabilities.VoiceName}"
            : "✕ 未检测到中文语音播报。请在 Windows 设置中安装中文语音包。";
        VoiceCapabilityText.Foreground = _speechCapabilities.HasChineseVoice
            ? ActiveBrush
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 79, 20));

        if (_speechCapabilities.IsReady)
        {
            StatusTitle.Text = "本地语音已就绪";
            StatusDescription.Text = "启动后可以直接说“你好贾维斯”；备用快捷键仍可用。";
            StartBackgroundButton.IsEnabled = true;
        }
        else
        {
            StatusTitle.Text = "本地语音尚未准备好";
            StatusDescription.Text = "请先补齐本地模型或中文语音播报。";
            StartBackgroundButton.IsEnabled = false;
        }
    }

    private void InitializeTrayIcon()
    {
        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("打开设置", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        _stopListeningMenuItem = new Forms.ToolStripMenuItem("立即停止麦克风") { Enabled = false };
        _stopListeningMenuItem.Click += (_, _) => Dispatcher.BeginInvoke(async () =>
            await StopBackgroundModeAsync(showSettings: false));
        _trayMenu.Items.Add(_stopListeningMenuItem);
        _trayMenu.Items.Add("退出屏幕陪练老师", null, (_, _) => Dispatcher.BeginInvoke(async () =>
            await ExitApplicationAsync()));

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "屏幕陪练老师（麦克风未启动）",
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

    private async Task ExitApplicationAsync()
    {
        _allowExit = true;
        if (_backgroundEnabled)
        {
            await StopBackgroundModeAsync(showSettings: false);
        }
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_backgroundEnabled && !_allowExit)
        {
            e.Cancel = true;
            Hide();
            _overlay.ShowWaitingForWakeWord();
            ShowTrayMessage(
                "屏幕陪练仍在后台",
                "直接说“你好贾维斯”，或从右下角菜单立即停止麦克风。 ");
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _voiceAssistant.WakeWordDetected -= VoiceAssistant_WakeWordDetected;
        _voiceAssistant.QuestionRecognized -= VoiceAssistant_QuestionRecognized;
        _voiceAssistant.StatusChanged -= VoiceAssistant_StatusChanged;
        _voiceAssistant.Failed -= VoiceAssistant_Failed;
        _hotkeys.VoiceRequested -= Hotkeys_VoiceRequested;
        _hotkeys.StopRequested -= Hotkeys_StopRequested;
        _hotkeys.Dispose();
        _backgroundCancellation?.Cancel();
        _voiceAssistant.StopAsync().GetAwaiter().GetResult();
        _backgroundCancellation?.Dispose();
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
