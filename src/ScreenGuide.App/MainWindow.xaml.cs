using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private readonly SelectedWindowCaptureService _windowCapture = new();
    private readonly SafeDesktopActionService _desktopActions = new();
    private readonly BailianConfigurationStore _bailianStore = new();
    private readonly CloudSharingAuthorization _cloudAuthorization = new();
    private readonly VoiceTurnRecoveryPolicy _turnRecoveryPolicy = VoiceTurnRecoveryPolicy.Default;
    private readonly IndexTtsLocalConfiguration _indexTtsConfiguration = IndexTtsLocalConfiguration.Create();
    private IndexTtsLocalProcess? _indexTtsProcess;

    private Forms.NotifyIcon? _notifyIcon;
    private Drawing.Icon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _stopListeningMenuItem;
    private SpeechCapabilityReport? _speechCapabilities;
    private CancellationTokenSource? _backgroundCancellation;
    private bool _backgroundEnabled;
    private bool _hotkeysReady;
    private bool _allowExit;
    private bool _initialized;
    private volatile bool _continuousConversationActive;
    private int _questionTurnInProgress;
    private ForegroundWindowContext _windowAtWake = ForegroundWindowContext.Unknown;
    private BailianConfiguration _bailianConfiguration = BailianConfiguration.Default;
    private string? _bailianApiKey;
    private IGuidanceProvider? _guidanceProvider;
    private ICloudSpeechService? _cloudSpeech;
    private Stopwatch? _recognitionTimer;
    private TimeSpan? _lastRecognitionElapsed;

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
        ApplyLocalBranding();
        InitializeTrayIcon();

        _voiceAssistant.WakeWordDetected += VoiceAssistant_WakeWordDetected;
        _voiceAssistant.QuestionRecognized += VoiceAssistant_QuestionRecognized;
        _voiceAssistant.StatusChanged += VoiceAssistant_StatusChanged;
        _voiceAssistant.Failed += VoiceAssistant_Failed;
        _overlay.SettingsRequested += Overlay_SettingsRequested;
        _overlay.StartVoiceRequested += Overlay_StartVoiceRequested;
        _overlay.StopVoiceRequested += Overlay_StopVoiceRequested;
        _overlay.ExitRequested += Overlay_ExitRequested;

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

        Dispatcher.BeginInvoke(() =>
        {
            _overlay.ShowStopped();
            if (App.ShowSettingsOnStartup)
            {
                Show();
                Activate();
                _overlay.HideToTray();
            }
            else
            {
                Hide();
                _overlay.ShowAvatar(expanded: true);
                if (GetCloudReadinessProblem() is null && _speechCapabilities is { IsReady: true })
                {
                    StartBackgroundButton_Click(StartBackgroundButton, new RoutedEventArgs());
                }
                else
                {
                    OpenSettings();
                }
            }
        });
    }

    private void Overlay_SettingsRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(OpenSettings);
    }

    private void Overlay_StartVoiceRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var readinessProblem = GetCloudReadinessProblem();
            if (readinessProblem is not null || _speechCapabilities is not { IsReady: true })
            {
                OpenSettings();
            }

            StartBackgroundButton_Click(StartBackgroundButton, new RoutedEventArgs());
        });
    }

    private void Overlay_StopVoiceRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(async () => await StopBackgroundModeAsync(showSettings: false));
    }

    private void Overlay_ExitRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(async () => await ExitApplicationAsync());
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
        _overlay.SetVoiceActive(true);
        StopBackgroundButton.IsEnabled = true;
        if (_stopListeningMenuItem is not null)
        {
            _stopListeningMenuItem.Enabled = true;
        }
        if (_notifyIcon is not null)
        {
            _notifyIcon.Text = "贾维斯（正在本机等待唤醒）";
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
                _overlay.ShowProcessing("上一轮还在处理", "长回答会继续播放；完成或异常后会自动恢复待命。");
                return;
            }

            RememberCurrentExternalWindow();
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
        _continuousConversationActive = true;
        RememberCurrentExternalWindow();
        _recognitionTimer = Stopwatch.StartNew();
        Dispatcher.BeginInvoke(() =>
        {
            StatusLight.Fill = ListeningBrush;
            StatusTitle.Text = "我在，请说";
            StatusDescription.Text = "说完后停顿一下；识别会自动结束。";
            LastVoiceStatusText.Text = "已听到唤醒词，正在识别这一句话。";
            _overlay.ShowListening();
        });
    }

    private void VoiceAssistant_QuestionRecognized(object? sender, VoiceQuestionEventArgs e)
    {
        RememberCurrentExternalWindow();
        _recognitionTimer?.Stop();
        _lastRecognitionElapsed = _recognitionTimer?.Elapsed;
        _recognitionTimer = null;
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
        var turnTimer = Stopwatch.StartNew();

        _overlay.ShowRecognized($"你说：{recognizedText}");
        LastVoiceStatusText.Text = $"刚刚听到：{recognizedText}";
        StatusLight.Fill = ActiveBrush;
        StatusTitle.Text = "已经听清";
        StatusDescription.Text = "正在判断问题类型；只有屏幕问题才会按授权读取当前前台窗口。";

        try
        {
            if (AssistantUiCommandParser.TryParse(recognizedText, out var uiCommand))
            {
                await ExecuteAssistantUiCommandAsync(uiCommand, turnToken);
                return;
            }

            if (ConversationExitPhraseMatcher.IsMatch(recognizedText))
            {
                _continuousConversationActive = false;
                _voiceAssistant.EndContinuousConversation();
                StatusLight.Fill = ActiveBrush;
                StatusTitle.Text = "连续对话已结束";
                StatusDescription.Text = "已安静退下；需要时再次说“你好贾维斯”。";
                LastVoiceStatusText.Text = "连续对话已结束，正在本机等待唤醒词。";
                return;
            }

            if (DesktopActionIntentParser.TryParse(recognizedText, out var actionIntent)
                && actionIntent is not null)
            {
                _overlay.ShowProcessing("正在执行你刚才说的操作", recognizedText);
                StatusTitle.Text = "正在执行明确指令";
                StatusDescription.Text = "只执行这一条语音指令；找不到可靠控件时不会盲目点击或输入。";
                var actionResult = await _desktopActions.ExecuteAsync(actionIntent, _windowAtWake, turnToken);
                if (actionResult.Succeeded)
                {
                    _overlay.ShowAnswer(actionResult.Message);
                    StatusLight.Fill = ActiveBrush;
                    StatusTitle.Text = "操作已完成";
                }
                else
                {
                    _overlay.ShowProblem(actionResult.Message);
                    StatusTitle.Text = "为了安全没有执行";
                }

                StatusDescription.Text = actionResult.Message;
                LastVoiceStatusText.Text = actionResult.Message;
                await SpeakAssistantTextAsync(actionResult.Message, turnToken);
                turnTimer.Stop();
                LastPerformanceText.Text = BuildPerformanceSummary(
                    "本机安全操作",
                    null,
                    null,
                    null,
                    turnTimer.Elapsed);
                return;
            }

            await AskBailianAndSpeakAsync(recognizedText, turnTimer, turnToken);
        }
        catch (OperationCanceledException) when (_turnRecoveryPolicy.IsTurnTimeout(
                   sessionToken.IsCancellationRequested,
                   turnCancellation.IsCancellationRequested))
        {
            const string timeoutMessage = "这次回答连续处理超过5分钟，已自动取消并恢复待命。你可以重新说“你好贾维斯”。";
            _overlay.ShowProblem(timeoutMessage);
            LastVoiceStatusText.Text = timeoutMessage;
            StatusLight.Fill = IdleBrush;
            StatusTitle.Text = "回答超时，正在恢复";
            StatusDescription.Text = "本轮云端请求和语音播放已取消，不会继续卡住麦克风。";
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
                if (_continuousConversationActive)
                {
                    _voiceAssistant.BeginFollowUpListening();
                    ShowContinuousConversationState();
                }
                else
                {
                    _voiceAssistant.ResumeWakeWordListening();
                    ShowWaitingState();
                }
            }
        }
    }

    private async Task ExecuteAssistantUiCommandAsync(
        AssistantUiCommand command,
        CancellationToken cancellationToken)
    {
        switch (command)
        {
            case AssistantUiCommand.OpenSettings:
                OpenSettings();
                LastVoiceStatusText.Text = "语音指令已执行：打开设置。";
                await SpeakAssistantTextAsync("设置已打开。", cancellationToken);
                break;
            case AssistantUiCommand.Compact:
                _overlay.ShowCompact();
                LastVoiceStatusText.Text = "语音指令已执行：缩小贾维斯。";
                await SpeakAssistantTextAsync("已经缩小。", cancellationToken);
                break;
            case AssistantUiCommand.Expand:
                _overlay.ShowExpanded();
                LastVoiceStatusText.Text = "语音指令已执行：展开贾维斯。";
                await SpeakAssistantTextAsync("已经展开。", cancellationToken);
                break;
            case AssistantUiCommand.Hide:
                _overlay.HideToTray();
                LastVoiceStatusText.Text = "语音指令已执行：隐藏贾维斯，语音仍在后台运行。";
                await SpeakAssistantTextAsync("已经隐藏，我仍在后台。", cancellationToken);
                break;
            case AssistantUiCommand.ExitApplication:
                LastVoiceStatusText.Text = "语音指令已执行：退出贾维斯。";
                await ExitApplicationAsync();
                break;
        }
    }

    private void ShowContinuousConversationState()
    {
        StatusLight.Fill = ListeningBrush;
        StatusTitle.Text = "连续对话中";
        StatusDescription.Text = "直接说下一句话；单独说“你退下吧”或“退出”即可安静结束。";
        LastVoiceStatusText.Text = "麦克风正在本机等待你的下一句话。";
        if (_notifyIcon is not null)
        {
            _notifyIcon.Text = "贾维斯（连续对话中）";
        }
        _overlay.ShowContinuousConversation();
    }

    private void ShowWaitingState()
    {
        StatusLight.Fill = ActiveBrush;
        StatusTitle.Text = "正在等待语音唤醒";
        StatusDescription.Text = "直接说“你好贾维斯”；上一轮无论成功或失败都不会阻塞下一轮。";
        LastVoiceStatusText.Text = "麦克风正在本机等待唤醒词。";
        if (_notifyIcon is not null)
        {
            _notifyIcon.Text = "贾维斯（正在本机等待唤醒）";
        }
        _overlay.ShowWaitingForWakeWord();
    }

    private async Task AskBailianAndSpeakAsync(
        string recognizedText,
        Stopwatch turnTimer,
        CancellationToken cancellationToken)
    {
        if (_guidanceProvider is null || _cloudSpeech is null)
        {
            throw new InvalidOperationException("阿里百炼尚未准备好。");
        }

        var route = GuidanceRouteClassifier.Classify(recognizedText);
        CapturedWindowFrame? frame = null;
        byte[]? imageForRequest = null;
        TimeSpan? captureElapsed = null;
        var windowTitle = _windowAtWake == ForegroundWindowContext.Unknown
            ? "未提供画面"
            : _windowAtWake.Title;

        if (route == GuidanceRoute.Vision)
        {
            RememberCurrentExternalWindow();
            if (!_cloudAuthorization.CanShareForegroundFrame())
            {
                RequestForegroundCaptureConsentForSession();
                if (!_cloudAuthorization.CanShareForegroundFrame())
                {
                    const string missingConsent = "这个问题需要查看屏幕，但你没有授权本次读取，所以我没有发送画面。";
                    _overlay.ShowProblem(missingConsent);
                    LastPerformanceText.Text = "已判断为屏幕问题；本次运行未授权，没有读取或上传画面。";
                    await SpeakAssistantTextAsync(missingConsent, cancellationToken);
                    return;
                }
            }

            if (!_windowAtWake.CanCapture || _windowAtWake.BelongsToCurrentProcess)
            {
                const string missingWindow = "我没有取得你正在使用的软件窗口。请先切到目标软件，再重新唤醒我提问。";
                _overlay.ShowProblem(missingWindow);
                LastPerformanceText.Text = "已判断为屏幕问题；没有取得外部前台窗口，未读取或上传画面。";
                await SpeakAssistantTextAsync(missingWindow, cancellationToken);
                return;
            }

            var captureTarget = new WindowCaptureTarget(_windowAtWake.Title, _windowAtWake.WindowHandle);
            _overlay.ShowProcessing("正在读取当前窗口", $"只读取一次：{captureTarget.DisplayName}");
            StatusTitle.Text = "正在读取一次画面";
            StatusDescription.Text = $"自动跟随前台窗口：{captureTarget.DisplayName}。截图不保存到硬盘。";

            var captureTimer = Stopwatch.StartNew();
            frame = await Task.Run(() => _windowCapture.CaptureOnce(captureTarget), cancellationToken);
            captureTimer.Stop();
            captureElapsed = captureTimer.Elapsed;
            windowTitle = captureTarget.DisplayName;
            var usePointerFocus = PointerFocusQuestionMatcher.IsMatch(recognizedText)
                && frame.PointerFocusJpegBytes is { Length: > 0 };
            imageForRequest = usePointerFocus ? frame.PointerFocusJpegBytes : frame.JpegBytes;

            _overlay.ShowProcessing(
                "正在请千问分析",
                usePointerFocus
                    ? "已自动裁剪鼠标附近的题目；正在流式回答"
                    : $"已取得 {frame.PixelWidth}×{frame.PixelHeight} 画面；正在流式回答");
            StatusTitle.Text = "千问正在分析画面";
            StatusDescription.Text = usePointerFocus
                ? "本次只发送鼠标附近的局部画面和问题文字；音频没有上传。"
                : "只把本次授权画面和问题文字发送到百炼；音频没有上传。";
        }
        else
        {
            _overlay.ShowProcessing("正在请 DeepSeek 回答", "普通问题不读取屏幕，正在流式生成");
            StatusTitle.Text = "DeepSeek 正在快速回答";
            StatusDescription.Text = "本次只发送问题文字，不读取、不上传屏幕画面。";
        }

        var answer = new StringBuilder();
        var modelTimer = Stopwatch.StartNew();
        TimeSpan? firstTokenElapsed = null;
        TimeSpan? firstAudioElapsed = null;
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
                if (firstAudioElapsed is null
                    && progress.Contains("正在用百炼语音回答", StringComparison.Ordinal))
                {
                    firstAudioElapsed = turnTimer.Elapsed;
                }
                StatusTitle.Text = progress;
                _overlay.ShowProcessing(progress, ShortenForOverlay(answer.ToString()));
            }));

        Exception? providerFailure = null;
        Exception? speechFailure = null;
        try
        {
            await foreach (var chunk in _guidanceProvider.StreamAnswerAsync(
                               new GuidanceRequest(recognizedText, windowTitle, imageForRequest),
                               cancellationToken))
            {
                firstTokenElapsed ??= modelTimer.Elapsed;
                answer.Append(chunk);
                await textChannel.Writer.WriteAsync(chunk, cancellationToken);
                var providerName = route == GuidanceRoute.Vision ? "千问视觉" : "DeepSeek";
                _overlay.ShowProcessing($"{providerName} 正在回答", ShortenForOverlay(answer.ToString()));
                LastVoiceStatusText.Text = $"{providerName}：{ShortenForOverlay(answer.ToString())}";
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            providerFailure = exception;
        }
        finally
        {
            modelTimer.Stop();
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
        turnTimer.Stop();
        var routeName = route == GuidanceRoute.Vision ? "屏幕问题 · 千问视觉" : "普通问题 · DeepSeek";
        var performanceSummary = BuildPerformanceSummary(
            routeName,
            captureElapsed,
            firstTokenElapsed,
            firstAudioElapsed,
            turnTimer.Elapsed);
        StatusLight.Fill = ActiveBrush;
        StatusTitle.Text = "回答完成";
        StatusDescription.Text = performanceSummary;
        LastPerformanceText.Text = performanceSummary;

        if (speechFailure is not null)
        {
            LastVoiceStatusText.Text = $"模型已回答，但百炼语音失败：{speechFailure.Message}；已改用 Windows 中文声音。";
            await _speech.SpeakChineseAsync(finalAnswer, cancellationToken);
        }
    }

    private async Task SpeakAssistantTextAsync(string text, CancellationToken cancellationToken)
    {
        if (_cloudSpeech is null)
        {
            await _speech.SpeakChineseAsync(text, cancellationToken);
            return;
        }

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        channel.Writer.TryWrite(text);
        channel.Writer.TryComplete();
        try
        {
            await _cloudSpeech.SpeakStreamAsync(
                channel.Reader.ReadAllAsync(cancellationToken),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastVoiceStatusText.Text = $"云端语音失败：{exception.Message}；已改用 Windows 中文声音。";
            await _speech.SpeakChineseAsync(text, cancellationToken);
        }
    }

    private string BuildPerformanceSummary(
        string routeName,
        TimeSpan? captureElapsed,
        TimeSpan? firstTokenElapsed,
        TimeSpan? firstAudioElapsed,
        TimeSpan totalElapsed)
    {
        var parts = new List<string> { routeName };
        if (_lastRecognitionElapsed is { } recognition)
        {
            parts.Add($"听写完成 {recognition.TotalSeconds:0.0}秒");
        }
        if (captureElapsed is { } capture)
        {
            parts.Add($"截屏 {capture.TotalSeconds:0.0}秒");
        }
        if (firstTokenElapsed is { } firstToken)
        {
            parts.Add($"模型首字 {firstToken.TotalSeconds:0.0}秒");
        }
        if (firstAudioElapsed is { } firstAudio)
        {
            parts.Add($"开始说话 {firstAudio.TotalSeconds:0.0}秒");
        }
        parts.Add($"本轮 {totalElapsed.TotalSeconds:0.0}秒");
        return string.Join(" · ", parts);
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
                StatusDescription.Text = "已经收到声音，正在用 Windows 中文识别和 Sherpa 双通道识别“你好贾维斯”。";
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
        ModelRoutingText.Text = $"普通问题：{_bailianConfiguration.TextModel}（关闭思考）\n"
                                + $"屏幕问题：{_bailianConfiguration.VisionModel}（按需单帧）\n"
                                + "对话记忆：本次运行内，DeepSeek约90万Token；千问视觉约22万Token";
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
            BailianStatusText.Text = $"✓ 密钥已安全保存在本机；文字：{_bailianConfiguration.TextModel}；视觉：{_bailianConfiguration.VisionModel}";
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

            BailianStatusText.Text = $"✓ 百炼连接成功；文字：{_bailianConfiguration.TextModel}；视觉：{_bailianConfiguration.VisionModel}";
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

    private void RequestForegroundCaptureConsentForSession()
    {
        var result = System.Windows.MessageBox.Show(
            "当你主动询问“这个界面、按钮或报错”时，贾维斯是否可以读取当时前台软件的一帧画面并发送到阿里百炼？\n\n"
            + "普通问题不会截图；不会读取整个桌面；截图不保存；授权只在本次运行有效。",
            "允许贾维斯理解当前窗口吗？",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        CloudConsentCheckBox.IsChecked = result == MessageBoxResult.Yes;
    }

    private void RememberCurrentExternalWindow()
    {
        var currentWindow = _foregroundWindow.GetCurrent();
        if (currentWindow.CanCapture && !currentWindow.BelongsToCurrentProcess)
        {
            _windowAtWake = currentWindow;
        }
    }

    private void CloudConsentCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (CloudConsentCheckBox.IsChecked == true)
        {
            _cloudAuthorization.GrantForForegroundWindowSession();
        }
        else
        {
            _cloudAuthorization.Revoke();
        }

        PrivacyScopeText.Text = _cloudAuthorization.IsGranted
            ? "本次运行已授权：屏幕问题自动读取当时前台窗口的一帧；切换软件无需重新选择。"
            : "当前未授权：普通语音问答仍可使用；任何软件窗口都不会被读取或上传。";
    }

    private void ConfigureCloudServices()
    {
        if (string.IsNullOrWhiteSpace(_bailianApiKey))
        {
            _guidanceProvider = null;
            _cloudSpeech = null;
            return;
        }

        _guidanceProvider = new BailianHybridGuideService(_bailianConfiguration, _bailianApiKey);
        var bailianSpeech = new BailianCosyVoiceService(_bailianConfiguration, _bailianApiKey);
        if (_indexTtsConfiguration.IsConfigured)
        {
            _indexTtsProcess ??= new IndexTtsLocalProcess(_indexTtsConfiguration);
            _ = _indexTtsProcess.TryStart();
            _cloudSpeech = new LocalFirstSpeechService(
                new IndexTtsLocalSpeechService(_indexTtsConfiguration.ServiceUri),
                bailianSpeech);
        }
        else
        {
            _cloudSpeech = bailianSpeech;
        }
    }

    private string? GetCloudReadinessProblem()
    {
        if (_guidanceProvider is null || _cloudSpeech is null)
        {
            return "请先在右侧粘贴阿里百炼 API Key，并点击“保存并测试”。";
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
        _continuousConversationActive = false;
        _overlay.SetVoiceActive(false);
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
            _notifyIcon.Text = "贾维斯（麦克风已停止）";
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
        _trayMenu.Items.Add("显示贾维斯", null, (_, _) => Dispatcher.Invoke(() => _overlay.ShowAvatar()));
        _trayMenu.Items.Add("打开设置", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        _stopListeningMenuItem = new Forms.ToolStripMenuItem("立即停止麦克风") { Enabled = false };
        _stopListeningMenuItem.Click += (_, _) => Dispatcher.BeginInvoke(async () =>
            await StopBackgroundModeAsync(showSettings: false));
        _trayMenu.Items.Add(_stopListeningMenuItem);
        _trayMenu.Items.Add("退出贾维斯", null, (_, _) => Dispatcher.BeginInvoke(async () =>
            await ExitApplicationAsync()));

        _trayIcon = Environment.ProcessPath is { } processPath
            ? Drawing.Icon.ExtractAssociatedIcon(processPath)
            : null;

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _trayIcon ?? Drawing.SystemIcons.Application,
            Text = "贾维斯（麦克风未启动）",
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        _notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(() => _overlay.ShowAvatar());
    }

    private void ApplyLocalBranding()
    {
        var imagePath = Path.Combine(AppContext.BaseDirectory, "jarvis-local.png");
        if (!File.Exists(imagePath))
        {
            return;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new System.Uri(imagePath);
        image.EndInit();
        image.Freeze();

        BrandImage.Source = image;
        BrandImage.Visibility = Visibility.Visible;
        BrandFallback.Visibility = Visibility.Collapsed;
        Icon = image;
    }

    private void OpenSettings()
    {
        if (!IsVisible)
        {
            Show();
        }

        WindowState = WindowState.Normal;
        Opacity = 1;
        Activate();
        _overlay.HideToTray();
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
        if (!_allowExit)
        {
            e.Cancel = true;
            Hide();
            if (_backgroundEnabled)
            {
                _overlay.ShowWaitingForWakeWord();
            }
            else
            {
                _overlay.ShowStopped();
            }
            _overlay.ShowAvatar();
            ShowTrayMessage(
                _backgroundEnabled ? "贾维斯仍在后台" : "设置已经收起",
                _backgroundEnabled
                    ? "直接说“你好贾维斯”，或从右下角菜单立即停止麦克风。"
                    : "贾维斯悬浮形象仍在桌面，可右键启动语音。 ");
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _voiceAssistant.WakeWordDetected -= VoiceAssistant_WakeWordDetected;
        _voiceAssistant.QuestionRecognized -= VoiceAssistant_QuestionRecognized;
        _voiceAssistant.StatusChanged -= VoiceAssistant_StatusChanged;
        _voiceAssistant.Failed -= VoiceAssistant_Failed;
        _overlay.SettingsRequested -= Overlay_SettingsRequested;
        _overlay.StartVoiceRequested -= Overlay_StartVoiceRequested;
        _overlay.StopVoiceRequested -= Overlay_StopVoiceRequested;
        _overlay.ExitRequested -= Overlay_ExitRequested;
        _hotkeys.VoiceRequested -= Hotkeys_VoiceRequested;
        _hotkeys.StopRequested -= Hotkeys_StopRequested;
        _hotkeys.Dispose();
        _backgroundCancellation?.Cancel();
        _voiceAssistant.StopAsync().GetAwaiter().GetResult();
        _backgroundCancellation?.Dispose();
        _notifyIcon?.Dispose();
        _trayIcon?.Dispose();
        _trayMenu?.Dispose();
        _indexTtsProcess?.Dispose();

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
