using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Voice.Windows;
using System.Windows;
using System.Windows.Media;

namespace ScreenGuide.DesktopClient;

public partial class OnboardingWindow : Window
{
    private readonly IDesktopApiClient _api;
    private readonly DesktopClientSettingsStore _settingsStore;
    private readonly IContinuousVoiceListener _voice;
    private DesktopClientSettings _settings;
    private int _step = 1;

    public OnboardingWindow(
        IDesktopApiClient api,
        DesktopClientSettingsStore settingsStore,
        DesktopClientSettings settings,
        IContinuousVoiceListener? voice = null)
    {
        InitializeComponent();
        _api = api;
        _settingsStore = settingsStore;
        _settings = settings;
        _voice = voice ?? new OfflineContinuousVoiceListener();
        Loaded += async (_, _) => await CheckCapabilitiesAsync();
        Closed += async (_, _) => await _voice.DisposeAsync();
    }

    public Guid? CreatedTaskId => null;

    public bool Completed { get; private set; }

    public bool StartWithConversation => false;

    private async Task CheckCapabilitiesAsync()
    {
        NextButton.IsEnabled = false;
        CodexCheckTitleText.Text = "正在检查本机中枢…";
        CodexCheckDetailText.Text = string.Empty;
        try
        {
            var status = await _api.GetSystemStatusAsync();
            var voice = _voice.GetCapabilityReport();
            CodexCheckIcon.Background = Brush("#EAF6F0");
            CodexCheckIconText.Foreground = Brush("#1F8A6A");
            CodexCheckIconText.Text = "✓";
            CodexCheckTitleText.Text = "本机中枢已经就绪";
            var coding = status.Codex.IsCompatible
                ? $"编程技能可用（版本 {status.Codex.Version}）"
                : "编程技能当前不可用，但不影响普通电脑操作";
            CodexCheckDetailText.Text = $"{voice.Message}\n{coding}";
            CodexHelpPanel.Visibility = status.Codex.IsCompatible
                ? Visibility.Collapsed
                : Visibility.Visible;
            ProjectStepMessageText.Text = voice.Message;
            ProjectStepMessageText.Foreground = Brush(
                voice.RecognitionModelAvailable && voice.MicrophoneCount > 0 ? "#1F8A6A" : "#A56210");
            NextButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            CodexCheckIconText.Text = "!";
            CodexCheckTitleText.Text = "无法连接本机中枢";
            CodexCheckDetailText.Text = UserFacingErrorMapper.Map(exception).Message;
            CodexHelpPanel.Visibility = Visibility.Visible;
        }
    }

    private async void RecheckCodexButton_Click(object sender, RoutedEventArgs e) =>
        await CheckCapabilitiesAsync();

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_step < 3)
        {
            _step++;
            RenderStep();
            return;
        }

        await CompleteAsync();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 1)
        {
            _step--;
            RenderStep();
        }
    }

    private async Task CompleteAsync()
    {
        await _voice.StopAsync();
        _settings = _settings with { OnboardingCompleted = true };
        await _settingsStore.SaveAsync(_settings);
        Completed = true;
        DialogResult = true;
        Close();
    }

    private void RenderStep()
    {
        CodexStepPanel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        ProjectStepPanel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        FirstTaskStepPanel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        StepIndicatorText.Text = $"第 {_step} 步，共 3 步";
        StepDot1.Fill = Brush(_step >= 1 ? "#44A394" : "#4A5960");
        StepDot2.Fill = Brush(_step >= 2 ? "#44A394" : "#4A5960");
        StepDot3.Fill = Brush(_step >= 3 ? "#44A394" : "#4A5960");
        BackButton.Visibility = _step > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _step == 3 ? "进入元枢" : "下一步";
        NextButton.IsEnabled = true;
    }

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));
}
