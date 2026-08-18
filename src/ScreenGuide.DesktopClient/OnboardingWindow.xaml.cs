using Microsoft.Win32;
using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;
using System.Windows;
using System.Windows.Media;

namespace ScreenGuide.DesktopClient;

public partial class OnboardingWindow : Window
{
    private readonly IDesktopApiClient _api;
    private readonly DesktopClientSettingsStore _settingsStore;
    private DesktopClientSettings _settings;
    private ProjectDto? _project;
    private int _step = 1;

    public OnboardingWindow(
        IDesktopApiClient api,
        DesktopClientSettingsStore settingsStore,
        DesktopClientSettings settings)
    {
        InitializeComponent();
        _api = api;
        _settingsStore = settingsStore;
        _settings = settings;
        Loaded += async (_, _) => await CheckCodexAsync();
    }

    public Guid? CreatedTaskId { get; private set; }

    public bool Completed { get; private set; }

    private async Task CheckCodexAsync()
    {
        NextButton.IsEnabled = false;
        CodexCheckTitleText.Text = "正在检查 Codex…";
        CodexCheckDetailText.Text = string.Empty;
        CodexHelpPanel.Visibility = Visibility.Collapsed;
        try
        {
            var status = await _api.GetSystemStatusAsync();
            if (status.Codex.IsCompatible)
            {
                CodexCheckIcon.Background = Brush("#EAF6F0");
                CodexCheckIconText.Foreground = Brush("#1F8A6A");
                CodexCheckIconText.Text = "✓";
                CodexCheckTitleText.Text = "Codex 可以使用";
                CodexCheckDetailText.Text = $"已检测到 Codex CLI {status.Codex.Version}，这个版本已通过 V0.1 兼容验证。";
                NextButton.IsEnabled = true;
            }
            else
            {
                CodexCheckIcon.Background = Brush("#FCECED");
                CodexCheckIconText.Foreground = Brush("#C94C4C");
                CodexCheckIconText.Text = "!";
                CodexCheckTitleText.Text = status.Codex.IsInstalled ? "Codex 版本不兼容" : "没有找到 Codex";
                CodexCheckDetailText.Text = status.Codex.Message;
                CodexHelpPanel.Visibility = Visibility.Visible;
            }
        }
        catch (Exception exception)
        {
            CodexCheckIconText.Text = "!";
            CodexCheckTitleText.Text = "无法连接本机服务";
            CodexCheckDetailText.Text = UserFacingErrorMapper.Map(exception).Message;
            CodexHelpPanel.Visibility = Visibility.Visible;
        }
    }

    private async void RecheckCodexButton_Click(object sender, RoutedEventArgs e) =>
        await CheckCodexAsync();

    private async void ChooseProjectButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择第一个 Git 项目",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _project = await _api.AddProjectAsync(new AddProjectRequestDto(dialog.FolderName));
            OnboardingProjectPathText.Text = _project.RootPath;
            ProjectStepMessageText.Text = "项目已授权。";
            ProjectStepMessageText.Foreground = Brush("#1F8A6A");
            NextButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            var error = UserFacingErrorMapper.Map(exception);
            ProjectStepMessageText.Text = error.Message;
            ProjectStepMessageText.Foreground = Brush("#C94C4C");
            NextButton.IsEnabled = false;
        }
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_step < 3)
        {
            _step++;
            RenderStep();
            return;
        }

        if (_project is null || string.IsNullOrWhiteSpace(FirstTaskInstructionTextBox.Text))
        {
            return;
        }

        try
        {
            var result = await _api.CreateTaskAsync(new CreateTaskRequestDto(
                _project.Id,
                FirstTaskInstructionTextBox.Text,
                "第一次尝试"));
            CreatedTaskId = result.TaskId;
            await CompleteAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                UserFacingErrorMapper.Map(exception).Message,
                "任务没有开始",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 1)
        {
            _step--;
            RenderStep();
        }
    }

    private async void SkipTaskButton_Click(object sender, RoutedEventArgs e) =>
        await CompleteAsync();

    private async Task CompleteAsync()
    {
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
        StepDot1.Fill = Brush(_step >= 1 ? "#5E91EA" : "#4A5960");
        StepDot2.Fill = Brush(_step >= 2 ? "#5E91EA" : "#4A5960");
        StepDot3.Fill = Brush(_step >= 3 ? "#5E91EA" : "#4A5960");
        BackButton.Visibility = _step > 1 ? Visibility.Visible : Visibility.Collapsed;
        SkipTaskButton.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _step == 3 ? "确认并开始" : "下一步";
        NextButton.IsEnabled = _step switch
        {
            1 => NextButton.IsEnabled,
            2 => _project is not null,
            _ => _project is not null
        };
        if (_step == 3)
        {
            FirstTaskProjectText.Text = _project is null
                ? "尚未选择项目"
                : $"将在“{_project.Name}”中执行";
        }
    }

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));
}
