using System.ComponentModel;
using System.Diagnostics;
using System.Media;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenGuide.App.Services;
using ScreenGuide.Core;
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
    private readonly ForegroundWindowContextService _foregroundWindow = new();
    private readonly WindowsWindowPickerService _windowPicker = new();
    private readonly SelectedWindowCaptureService _windowCapture = new();
    private readonly BailianConfigurationStore _bailianStore = new();
    private readonly CloudSharingAuthorization _cloudAuthorization = new();
    private readonly VoiceTurnRecoveryPolicy _turnRecoveryPolicy = VoiceTurnRecoveryPolicy.Default;

    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _stopListeningMenuItem;
    private SpeechCapabilityReport? _speechCapabilities;
    private CancellationTokenSource? _backgroundCancellation;
    private bool _backgroundEnabled;
    private bool _hotkeysReady;
    private bool _allowExit;
    private bool _initialized;
    private int _questionTurnInProgress;
    private ForegroundWindowContext _windowAtWake = ForegroundWindowContext.Unknown;
    private BailianConfiguration _bailianConfiguration = BailianConfiguration.Default;
    private string? _bailianApiKey;
    private IGuidanceProvider? _guidanceProvider;
    private ICloudSpeechService? _cloudSpeech;
    private WindowSelectionResult? _selectedWindow;

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
        _windowPicker.SelectedTargetClosed += WindowPicker_SelectedTargetClosed;

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

        LoadBailianConfiguration();
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

        var cloudProblem = GetCloudReadinessProblem();
        if (cloudProblem is not null)
        {
            System.Windows.MessageBox.Show(
                cloudProblem,
                "还差一步才能开始 AI 陪练",
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
            if (Volatile.Read(ref _questionTurnInProgress) != 0)
            {
                _overlay.ShowProcessing("上一轮还在处理", "最长等待30秒；结束或超时后会自动恢复待命。");
                return;
            }

            _voiceAssistant.BeginQuestionListening();
            return;
        }

        _overlay.ShowStopped();
        ShowTrayMessage("麦克风当前已停止", "请从右下角托盘图标打开设置，然后重新启动后台陪练。");
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
        _windowAtWake = _foregroundWindow.GetCurrent();
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

        if (Interlocked.CompareExchange(ref _questionTurnInProgress, 1, 0) != 0)
        {
            _overlay.ShowProcessing("上一轮还在处理", "请稍等；本轮结束或超时后会自动恢复待命。");
            return;
        }

        var sessionToken = _backgroundCancellation.Token;
        using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        turnCancellation.CancelAfter(_turnRecoveryPolicy.TurnTimeout);
        var turnToken = turnCancellation.Token;

        _overlay.ShowRecognized($"你说：{recognizedText}");
        LastVoiceStatusText.Text = $"刚刚听到：{recognizedText}";
        StatusLight.Fill = ActiveBrush;
        StatusTitle.Text = "已经听清";
        StatusDescription.Text = "正在判断能否本机快速回答，否则读取一次授权窗口。";

        try
        {
            var localReply = TryBuildLocalReply(recognizedText);
            if (localReply is not null)
            {
                _overlay.ShowAnswer(localReply);
                await _speech.SpeakChineseAsync(localReply, turnToken);
            }
            else
            {
                await AskBailianAndSpeakAsync(recognizedText, turnToken);
            }
        }
        catch (OperationCanceledException) when (_turnRecoveryPolicy.IsTurnTimeout(
                   sessionToken.IsCancellationRequested,
                   turnCancellation.IsCancellationRequested))
        {
            const string timeoutMessage = "这次回答等待超过30秒，已自动取消并恢复待命。你可以重新说“你好贾维斯”。";
            _overlay.ShowProblem(timeoutMessage);
            LastVoiceStatusText.Text = timeoutMessage;
            StatusLight.Fill = IdleBrush;
            StatusTitle.Text = "回答超时，正在恢复";
            StatusDescription.Text = "本轮云端请求和语音播放已取消，不会继续卡住麦克风。";
            SystemSounds.Exclamation.Play();

            using var recoverySpeechCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
            recoverySpeechCancellation.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await _speech.SpeakChineseAsync("这次等待超时，已经恢复待命。", recoverySpeechCancellation.Token);
            }
            catch
            {
                // The persistent overlay still explains the timeout.
            }
        }
        catch (OperationCanceledException)
        {
            // The whole background session is stopping.
        }
        catch (Exception exception)
        {
            var message = $"这次没有完成回答：{exception.Message}";
            _overlay.ShowProblem(message);
            LastVoiceStatusText.Text = message;
            StatusTitle.Text = "这次回答失败";
            StatusDescription.Text = exception.Message;
            try
            {
                using var failureSpeechCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
                failureSpeechCancellation.CancelAfter(TimeSpan.FromSeconds(5));
                await _speech.SpeakChineseAsync(
                    "这次回答没有完成，已经恢复待命。",
                    failureSpeechCancellation.Token);
            }
            catch
            {
                // The visible overlay still contains the failure reason.
            }
        }
        finally
        {
            Interlocked.Exchange(ref _questionTurnInProgress, 0);
            if (_turnRecoveryPolicy.ShouldResumeListening(
                    _backgroundEnabled,
                    sessionToken.IsCancellationRequested))
            {
                _voiceAssistant.ResumeWakeWordListening();
                ShowWaitingState();
            }
        }
    }

    private void ShowWaitingState()
    {
        StatusLight.Fill = ActiveBrush;
        StatusTitle.Text = "正在等待语音唤醒";
        StatusDescription.Text = "直接说“你好贾维斯”；上一轮无论成功或失败都不会阻塞下一轮。";
        LastVoiceStatusText.Text = "麦克风正在本机等待唤醒词。";
        if (_notifyIcon is not null)
        {
            _notifyIcon.Text = "屏幕陪练老师（正在本机等待唤醒）";
        }
        _overlay.ShowWaitingForWakeWord();
    }

    private string? TryBuildLocalReply(string recognizedText)
    {
        if (recognizedText.Contains("哪个界面", StringComparison.Ordinal)
            || recognizedText.Contains("什么界面", StringComparison.Ordinal)
            || recognizedText.Contains("哪个窗口", StringComparison.Ordinal))
        {
            return _windowAtWake == ForegroundWindowContext.Unknown
                ? "我已经听清问题，但这次没有取得前台窗口名称。"
                : $"你现在位于，{_windowAtWake.Title}，窗口。";
        }

        return null;
    }

    private async Task AskBailianAndSpeakAsync(string recognizedText, CancellationToken cancellationToken)
    {
        if (_guidanceProvider is null || _cloudSpeech is null || _selectedWindow is null)
        {
            throw new InvalidOperationException("阿里百炼或授权窗口尚未准备好。");
        }

        if (!_cloudAuthorization.CanShareFrameFrom(_selectedWindow.DisplayName))
        {
            throw new InvalidOperationException("发送画面前需要在设置中勾选明确授权。");
        }

        _overlay.ShowProcessing("正在读取授权窗口", $"只读取一次：{_selectedWindow.DisplayName}");
        StatusTitle.Text = "正在读取一次画面";
        StatusDescription.Text = $"目标：{_selectedWindow.DisplayName}。截图不保存到硬盘。";

        var captureTimer = Stopwatch.StartNew();
        var frame = await Task.Run(() => _windowCapture.CaptureOnce(_selectedWindow), cancellationToken);
        captureTimer.Stop();

        _overlay.ShowProcessing("正在请千问分析", $"已取得 {frame.PixelWidth}×{frame.PixelHeight} 画面；正在流式回答");
        StatusTitle.Text = "千问正在分析画面";
        StatusDescription.Text = "画面和问题文字已发送到华北2（北京）百炼；音频没有上传。";

        var answer = new StringBuilder();
        var textChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        var speechTask = _cloudSpeech.SpeakStreamAsync(
            textChannel.Reader.ReadAllAsync(cancellationToken),
            cancellationToken,
            progress => Dispatcher.BeginInvoke(() =>
            {
                StatusTitle.Text = progress;
                _overlay.ShowProcessing(progress, ShortenForOverlay(answer.ToString()));
            }));

        Exception? providerFailure = null;
        Exception? speechFailure = null;
        try
        {
            await foreach (var chunk in _guidanceProvider.StreamAnswerAsync(
                               new GuidanceRequest(recognizedText, _selectedWindow.DisplayName, frame.JpegBytes),
                               cancellationToken))
            {
                answer.Append(chunk);
                await textChannel.Writer.WriteAsync(chunk, cancellationToken);
                _overlay.ShowProcessing("千问正在回答", ShortenForOverlay(answer.ToString()));
                LastVoiceStatusText.Text = $"千问：{ShortenForOverlay(answer.ToString())}";
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            providerFailure = exception;
        }
        finally
        {
            textChannel.Writer.TryComplete();
        }

        try
        {
            await speechTask.WaitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            speechFailure = exception;
        }

        if (providerFailure is not null)
        {
            throw new InvalidOperationException(providerFailure.Message, providerFailure);
        }

        var finalAnswer = answer.ToString().Trim();
        if (string.IsNullOrWhiteSpace(finalAnswer))
        {
            throw new InvalidOperationException("千问没有返回回答，请稍后重试。");
        }

        _overlay.ShowAnswer(finalAnswer);
        StatusLight.Fill = ActiveBrush;
        StatusTitle.Text = "回答完成";
        StatusDescription.Text = $"画面读取耗时 {captureTimer.Elapsed.TotalSeconds:0.0} 秒；截图只在内存中使用。";

        if (speechFailure is not null)
        {
            LastVoiceStatusText.Text = $"千问已回答，但百炼语音失败：{speechFailure.Message}；已改用 Windows 中文声音。";
            await _speech.SpeakChineseAsync(finalAnswer, cancellationToken);
        }
    }

    private static string ShortenForOverlay(string text)
    {
        var normalized = text.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 100 ? normalized : normalized[..100] + "…";
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
            if (e.Message.Contains("已检测到麦克风声音", StringComparison.Ordinal))
            {
                StatusLight.Fill = ListeningBrush;
                StatusTitle.Text = "麦克风输入正常";
                StatusDescription.Text = "已经收到声音，正在用两种本地方式识别“你好贾维斯”。";
                _overlay.ShowProcessing("麦克风已经听到声音", "正在识别“你好贾维斯”…");
            }
            else if (e.Message.Contains("没有听清", StringComparison.Ordinal))
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

    private void LoadBailianConfiguration()
    {
        _bailianConfiguration = _bailianStore.Load();
        try
        {
            _bailianApiKey = _bailianStore.ReadApiKey();
        }
        catch (Exception exception)
        {
            BailianStatusText.Text = $"Windows 凭据读取失败：{exception.Message}";
            BailianStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 79, 20));
            return;
        }

        ConfigureCloudServices();
        if (_bailianApiKey is null)
        {
            BailianStatusText.Text = "尚未配置。请粘贴华北2（北京）地域的百炼 API Key。";
            BailianStatusText.Foreground = IdleBrush;
        }
        else
        {
            BailianStatusText.Text = $"✓ 密钥已安全保存在本机；模型：{_bailianConfiguration.VisionModel}";
            BailianStatusText.Foreground = ActiveBrush;
        }
    }

    private async void SaveBailianButton_Click(object sender, RoutedEventArgs e)
    {
        SaveBailianButton.IsEnabled = false;
        BailianStatusText.Text = "正在保存并进行一次最小连接测试…";
        BailianStatusText.Foreground = ListeningBrush;

        try
        {
            var enteredKey = BailianApiKeyBox.Password.Trim();
            if (!string.IsNullOrWhiteSpace(enteredKey))
            {
                _bailianStore.Save(_bailianConfiguration, enteredKey);
                _bailianApiKey = enteredKey;
                BailianApiKeyBox.Clear();
            }
            else if (_bailianApiKey is null)
            {
                throw new InvalidOperationException("请先粘贴百炼 API Key。");
            }

            ConfigureCloudServices();
            var testText = new StringBuilder();
            await foreach (var chunk in _guidanceProvider!.StreamAnswerAsync(
                               new GuidanceRequest("只回答四个字：连接成功", "连接测试（不含画面）", null),
                               CancellationToken.None))
            {
                testText.Append(chunk);
                if (testText.Length >= 4)
                {
                    break;
                }
            }

            if (testText.Length == 0)
            {
                throw new InvalidOperationException("百炼没有返回测试结果。");
            }

            BailianStatusText.Text = $"✓ 百炼连接成功；模型：{_bailianConfiguration.VisionModel}";
            BailianStatusText.Foreground = ActiveBrush;
        }
        catch (Exception exception)
        {
            BailianStatusText.Text = $"连接失败：{exception.Message}";
            BailianStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 79, 20));
        }
        finally
        {
            SaveBailianButton.IsEnabled = true;
        }
    }

    private async void SelectWindowButton_Click(object sender, RoutedEventArgs e)
    {
        SelectWindowButton.IsEnabled = false;
        try
        {
            var result = await _windowPicker.PickWindowAsync(new WindowInteropHelper(this).Handle);
            if (result is null)
            {
                if (_selectedWindow is { CanCapture: true })
                {
                    SelectedWindowText.Text = $"✓ 继续使用：{_selectedWindow.DisplayName}";
                    SelectedWindowText.Foreground = ActiveBrush;
                    CloudConsentCheckBox.IsEnabled = true;
                }
                else
                {
                    _selectedWindow = null;
                    _cloudAuthorization.Revoke();
                    CloudConsentCheckBox.IsChecked = false;
                    CloudConsentCheckBox.IsEnabled = false;
                    SelectedWindowText.Text = "尚未选择（刚才已取消）";
                    SelectedWindowText.Foreground = IdleBrush;
                }
                return;
            }

            _selectedWindow = result;
            CloudConsentCheckBox.IsChecked = false;
            CloudConsentCheckBox.IsEnabled = false;
            _cloudAuthorization.Revoke();
            if (!result.CanCapture)
            {
                SelectedWindowText.Text = $"{result.DisplayName}（无法准确定位，请恢复窗口后重选）";
                SelectedWindowText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 79, 20));
                return;
            }

            SelectedWindowText.Text = $"✓ {result.DisplayName}";
            SelectedWindowText.Foreground = ActiveBrush;
            CloudConsentCheckBox.IsEnabled = true;
            PrivacyScopeText.Text = "已选择窗口，但只有勾选下方授权后，每次提问才会读取一帧。";
        }
        catch (Exception exception)
        {
            _selectedWindow = null;
            SelectedWindowText.Text = $"选择失败：{exception.Message}";
            SelectedWindowText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 79, 20));
        }
        finally
        {
            SelectWindowButton.IsEnabled = true;
        }
    }

    private void CloudConsentCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (CloudConsentCheckBox.IsChecked == true && _selectedWindow is { CanCapture: true })
        {
            _cloudAuthorization.GrantForWindow(_selectedWindow.DisplayName);
        }
        else
        {
            _cloudAuthorization.Revoke();
            if (_selectedWindow is not { CanCapture: true } && CloudConsentCheckBox.IsChecked == true)
            {
                CloudConsentCheckBox.IsChecked = false;
            }
        }

        PrivacyScopeText.Text = _cloudAuthorization.IsGranted
            ? "授权已开启：每次提问只读取一帧；麦克风音频仍不上传，截图不写入硬盘。"
            : _selectedWindow is { CanCapture: true }
                ? "授权未开启：已选窗口，但程序不会读取或发送画面。"
                : "请先成功选择一个软件窗口，之后才能勾选上传授权。";
    }

    private void WindowPicker_SelectedTargetClosed(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _selectedWindow = null;
            _cloudAuthorization.Revoke();
            CloudConsentCheckBox.IsChecked = false;
            CloudConsentCheckBox.IsEnabled = false;
            SelectedWindowText.Text = "原授权窗口已关闭，请重新选择";
            SelectedWindowText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 79, 20));
        });
    }

    private void ConfigureCloudServices()
    {
        if (string.IsNullOrWhiteSpace(_bailianApiKey))
        {
            _guidanceProvider = null;
            _cloudSpeech = null;
            return;
        }

        _guidanceProvider = new BailianVisionGuideService(_bailianConfiguration, _bailianApiKey);
        _cloudSpeech = new BailianCosyVoiceService(_bailianConfiguration, _bailianApiKey);
    }

    private string? GetCloudReadinessProblem()
    {
        if (_guidanceProvider is null || _cloudSpeech is null)
        {
            return "请先在右侧粘贴阿里百炼 API Key，并点击“保存并测试”。";
        }

        if (_selectedWindow is not { CanCapture: true })
        {
            return "请先点击“选择软件窗口”，选择你希望我观察的软件。";
        }

        if (!_cloudAuthorization.CanShareFrameFrom(_selectedWindow.DisplayName))
        {
            return "请阅读并勾选窗口单帧上传授权。没有明确授权，程序不会读取画面。";
        }

        return null;
    }

    private void VoiceAssistant_Failed(object? sender, VoiceStatusEventArgs e)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (_backgroundEnabled)
            {
                await StopBackgroundModeAsync(showSettings: false);
            }

            var message = $"麦克风意外停止：{e.Message}。请打开设置后重新启动。";
            ShowVoiceProblem(message);
            _overlay.ShowProblem(message);
        });
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
            ShowTrayMessage("后台陪练已停止", "麦克风已经关闭；右上角状态条会一直提示，直到重新启动。 ");
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
        _windowPicker.SelectedTargetClosed -= WindowPicker_SelectedTargetClosed;
        _hotkeys.VoiceRequested -= Hotkeys_VoiceRequested;
        _hotkeys.StopRequested -= Hotkeys_StopRequested;
        _hotkeys.Dispose();
        _windowPicker.Dispose();
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
