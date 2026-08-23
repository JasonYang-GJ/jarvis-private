using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Voice.Windows;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace ScreenGuide.DesktopClient;

public partial class MainWindow : Window
{
    private readonly IDesktopApiClient _api;
    private readonly DesktopHostProcessManager _hostProcessManager;
    private readonly DesktopClientSettingsStore _settingsStore;
    private readonly WindowsStartupService _startupService;
    private readonly TrayIconService _tray;
    private readonly DesktopNotificationCoordinator _notifications;
    private readonly IContinuousVoiceListener _voice;
    private readonly WindowsSpeechOutput _speechOutput = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<ProjectDto> _projects = [];
    private IReadOnlyList<DesktopApplicationDto> _desktopApplications = [];
    private IReadOnlyList<TaskSummaryDto> _tasks = [];
    private IReadOnlyList<ConversationSummaryDto> _conversations = [];
    private SystemStatusDto? _systemStatus;
    private Guid? _selectedTaskId;
    private Guid? _selectedConversationId;
    private Guid? _assistantConversationId;
    private AssistantIntentPlanDto? _pendingAssistantPlan;
    private CancellationTokenSource? _windowObservationCancellation;
    private string? _activeWindowObservationId;
    private bool _isRefreshing;
    private bool _isHostOnline;
    private bool _forceClose;
    private bool _voiceCommandBusy;
    private VoiceUtterance? _queuedVoiceUtterance;
    private CancellationTokenSource? _activeVoiceCommandCancellation;
    private CancellationTokenSource? _speechCancellation;
    private string? _activeSpeechText;
    private string? _lastAssistantSpeechText;
    private DateTimeOffset _lastAssistantSpeechEndedAt;
    private string? _lastVoiceUtterance;
    private DateTimeOffset _lastVoiceUtteranceAt;
    private DesktopClientSettings _settings;

    public MainWindow(
        IDesktopApiClient api,
        DesktopHostProcessManager? hostProcessManager = null,
        DesktopClientSettingsStore? settingsStore = null,
        WindowsStartupService? startupService = null,
        TrayIconService? tray = null,
        DesktopClientSettings? settings = null,
        IContinuousVoiceListener? voice = null)
    {
        InitializeComponent();
        _api = api;
        _hostProcessManager = hostProcessManager ?? new DesktopHostProcessManager(api);
        _settingsStore = settingsStore ?? new DesktopClientSettingsStore();
        _startupService = startupService ?? new WindowsStartupService();
        _tray = tray ?? new TrayIconService();
        _settings = settings ?? new DesktopClientSettings();
        _voice = voice ?? new OfflineContinuousVoiceListener();
        _voice.PartialTextChanged += Voice_PartialTextChanged;
        _voice.UtteranceRecognized += Voice_UtteranceRecognized;
        _voice.StateChanged += Voice_StateChanged;
        _notifications = new DesktopNotificationCoordinator(_tray, _settingsStore);
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ProductVersionText.Text = $"版本 {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.2.0"}";
        RenderVoiceStatus();
        await RefreshAllAsync(showErrors: true);
        await StartContinuousVoiceAsync();
        _refreshTimer.Start();
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _windowObservationCancellation?.Cancel();
        _windowObservationCancellation?.Dispose();
        _activeVoiceCommandCancellation?.Cancel();
        _activeVoiceCommandCancellation?.Dispose();
        _speechCancellation?.Cancel();
        _speechCancellation?.Dispose();
        _lifetime.Cancel();
        _voice.PartialTextChanged -= Voice_PartialTextChanged;
        _voice.UtteranceRecognized -= Voice_UtteranceRecognized;
        _voice.StateChanged -= Voice_StateChanged;
        await _voice.DisposeAsync();
        _lifetime.Dispose();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_forceClose)
        {
            _ = _voice.StopAsync();
            return;
        }

        if (_settings.RunInBackground && _settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            StatusBarText.Text = "元枢正在后台运行。";
            return;
        }

        _forceClose = true;
        _ = _voice.StopAsync();
        Dispatcher.BeginInvoke(() => System.Windows.Application.Current.Shutdown());
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e) =>
        await RefreshAllAsync(showErrors: false);

    private async Task RefreshAllAsync(bool showErrors)
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            var dashboardTask = _api.GetDashboardAsync(_lifetime.Token);
            var projectsTask = _api.ListProjectsAsync(_lifetime.Token);
            var tasksTask = _api.ListTasksAsync(cancellationToken: _lifetime.Token);
            var desktopApplicationsTask = _api.ListDesktopApplicationsAsync(_lifetime.Token);
            var conversationsTask = _api.ListConversationsAsync(_lifetime.Token);
            await Task.WhenAll(
                dashboardTask,
                projectsTask,
                tasksTask,
                desktopApplicationsTask,
                conversationsTask);
            var dashboard = await dashboardTask;
            _projects = await projectsTask;
            _tasks = await tasksTask;
            _desktopApplications = await desktopApplicationsTask;
            _conversations = await conversationsTask;
            _systemStatus = dashboard.System;
            SetHostOnline(true);
            RenderDashboard(dashboard);
            RenderProjects();
            RenderTaskSelectors();
            RenderDesktopActions();
            RenderHistory();
            RenderConversations();
            RenderSettings();
            _settings = await _notifications.ProcessAsync(_tasks, _settings, _lifetime.Token);
            if (_selectedTaskId is { } taskId)
            {
                await LoadTaskDetailsAsync(taskId, navigate: false);
            }
            else
            {
                var current = dashboard.WaitingTasks.FirstOrDefault()
                    ?? dashboard.ActiveTasks.FirstOrDefault();
                if (current is not null)
                {
                    _selectedTaskId = current.Id;
                    await LoadTaskDetailsAsync(current.Id, navigate: false);
                }
            }

            if (_selectedConversationId is { } conversationId)
            {
                await LoadConversationAsync(conversationId, navigate: false);
            }

            if (_assistantConversationId is { } assistantConversationId)
            {
                await RefreshAssistantConversationAsync(assistantConversationId);
            }

            StatusBarText.Text = $"已连接本机服务 · 最后刷新 {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetHostOnline(false);
            _tray.UpdateState(TrayVisualState.Error);
            StatusBarText.Text = "本机中枢暂时离线，正在尝试恢复连接。";
            if (showErrors)
            {
                ShowError("本机任务服务暂时无法连接。", exception);
            }

            try
            {
                await _hostProcessManager.EnsureRunningAsync(_lifetime.Token);
            }
            catch
            {
            }
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void RenderDashboard(DashboardDto dashboard)
    {
        ActiveCountText.Text = dashboard.ActiveTaskCount.ToString();
        WaitingCountText.Text = dashboard.WaitingTaskCount.ToString();
        ProjectCountText.Text = dashboard.AuthorizedProjectCount.ToString();
        TodayCompletedCountText.Text = dashboard.CompletedTodayCount.ToString();
        FailedCountText.Text = dashboard.FailedTaskCount.ToString();
        RenderVoiceStatus();
        DashboardActiveList.ItemsSource = dashboard.WaitingTasks
            .Concat(dashboard.ActiveTasks)
            .Select(TaskRow.From)
            .ToArray();
        DashboardRecentList.ItemsSource = dashboard.RecentTasks.Select(TaskRow.From).ToArray();
        HostStatusText.Text = dashboard.System.HostOnline ? "本机中枢在线" : "本机中枢离线";
        CodexStatusText.Text = dashboard.System.Codex.IsCompatible
            ? $"Codex {dashboard.System.Codex.Version} 可用"
            : dashboard.System.Codex.IsInstalled ? "Codex 版本不兼容" : "未找到 Codex";
        CodexStatusPill.Background = BrushFrom(
            dashboard.System.Codex.IsCompatible ? "#EAF6F0" : "#FFF3E5");
        CodexStatusText.Foreground = BrushFrom(
            dashboard.System.Codex.IsCompatible ? "#1F8A6A" : "#A56210");
    }

    private void RenderProjects()
    {
        var rows = _projects.Select(ProjectRow.From).ToArray();
        var selectedId = (ProjectsListView.SelectedItem as ProjectRow)?.Id;
        ProjectsListView.ItemsSource = rows;
        ProjectsListView.SelectedItem = rows.FirstOrDefault(row => row.Id == selectedId);
    }

    private void RenderTaskSelectors()
    {
        var authorized = _projects
            .Where(project => project.AuthorizationState == "Authorized")
            .ToArray();
        PreserveProjectSelection(NewTaskProjectComboBox, authorized);
        NewTaskSubmitButton.IsEnabled = authorized.Length > 0 && _isHostOnline;
    }

    private static void PreserveProjectSelection(WpfComboBox comboBox, ProjectDto[] projects)
    {
        var selectedId = (comboBox.SelectedItem as ProjectDto)?.Id;
        comboBox.ItemsSource = projects;
        comboBox.SelectedItem = projects.FirstOrDefault(project => project.Id == selectedId)
            ?? projects.FirstOrDefault();
    }

    private void RenderDesktopActions()
    {
        var selectedId = (DesktopApplicationComboBox.SelectedItem as DesktopApplicationDto)?.Id;
        DesktopApplicationComboBox.ItemsSource = _desktopApplications;
        DesktopApplicationComboBox.SelectedItem = _desktopApplications.FirstOrDefault(item =>
            item.Id == selectedId) ?? _desktopApplications.FirstOrDefault();
        OpenDesktopApplicationButton.IsEnabled = _isHostOnline && _desktopApplications.Count > 0;
        OpenDesktopWebsiteButton.IsEnabled = _isHostOnline;
    }

    private void RenderHistory()
    {
        var filter = (HistoryFilterComboBox.SelectedItem as WpfComboBoxItem)?.Tag?.ToString();
        var filtered = string.IsNullOrWhiteSpace(filter)
            ? _tasks
            : _tasks.Where(task =>
                string.Equals(task.Status, filter, StringComparison.OrdinalIgnoreCase)
                || string.Equals(task.VerificationStatus, filter, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        HistoryListView.ItemsSource = filtered.Select(HistoryRow.From).ToArray();
    }

    private void RenderConversations()
    {
        var rows = _conversations.Select(ConversationRow.From).ToArray();
        ConversationListBox.ItemsSource = rows;
        ConversationListBox.SelectedItem = rows.FirstOrDefault(row => row.Id == _selectedConversationId);
        if (_selectedConversationId is null && rows.Length > 0)
        {
            _selectedConversationId = rows[0].Id;
            ConversationListBox.SelectedItem = rows[0];
        }

        var online = _isHostOnline && _systemStatus?.Codex.IsCompatible == true;
        ConversationInputTextBox.IsEnabled = online && _selectedConversationId is not null;
        SendConversationButton.IsEnabled = ConversationInputTextBox.IsEnabled;
    }

    private async Task LoadConversationAsync(Guid conversationId, bool navigate)
    {
        var details = await _api.GetConversationAsync(conversationId, _lifetime.Token);
        if (details is null)
        {
            return;
        }

        _selectedConversationId = conversationId;
        ConversationTitleText.Text = details.Summary.Title;
        ConversationStateText.Text = ConversationStatusLabel(details.Summary.Status, details.Summary.FailureMessage);
        var messages = details.Messages.Select(ConversationMessageRow.From).ToArray();
        ConversationMessagesList.ItemsSource = messages;
        ConversationMessagesList.Visibility = messages.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ConversationEmptyPanel.Visibility = messages.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var responding = details.Summary.Status == "Responding";
        StopConversationButton.Visibility = responding ? Visibility.Visible : Visibility.Collapsed;
        SendConversationButton.IsEnabled = !responding && _isHostOnline && _systemStatus?.Codex.IsCompatible == true;
        ConversationInputTextBox.IsEnabled = SendConversationButton.IsEnabled;
        if (messages.Length > 0)
        {
            ConversationMessagesList.ScrollIntoView(messages[^1]);
        }

        if (navigate)
        {
            ShowPage(AppPage.Conversations);
        }
    }

    private void RenderSettings()
    {
        if (_systemStatus is null)
        {
            return;
        }

        SettingsHostStatusText.Text = _systemStatus.HostOnline ? "在线" : "离线";
        SettingsCodexStatusText.Text = _systemStatus.Codex.Message;
        SettingsCodexVersionText.Text = _systemStatus.Codex.Version ?? "未检测到";
        SettingsVoiceStatusText.Text = _voice.IsListening
            ? "自动监听已开启；音频只在本机内存中识别。"
            : _voice.GetCapabilityReport().Message;
        SettingsDatabaseStatusText.Text = _systemStatus.DatabaseStatus == "Ready" ? "正常" : _systemStatus.DatabaseStatus;
        SettingsDeviceIdText.Text = _systemStatus.DeviceId;
        SettingsDataPathText.Text = _systemStatus.DataDirectory;
        StartWithWindowsCheckBox.IsChecked = _settings.StartWithWindows;
        RunInBackgroundCheckBox.IsChecked = _settings.RunInBackground;
        CloseToTrayCheckBox.IsChecked = _settings.CloseToTray;
        NotificationsEnabledCheckBox.IsChecked = _settings.NotificationsEnabled;
    }

    private async Task LoadTaskDetailsAsync(Guid taskId, bool navigate)
    {
        var details = await _api.GetTaskAsync(taskId, _lifetime.Token);
        if (details is null)
        {
            return;
        }

        _selectedTaskId = taskId;
        CurrentTaskEmptyPanel.Visibility = Visibility.Collapsed;
        CurrentTaskContent.Visibility = Visibility.Visible;
        CurrentTaskTitleText.Text = details.Summary.Title;
        CurrentTaskStatusText.Text = StatusLabel(details.Summary.Status);
        CurrentTaskProjectText.Text = $"项目：{details.Summary.ProjectName}";
        CurrentTaskDurationText.Text = DurationText(details.Summary);
        CurrentInstructionText.Text = details.Instruction;
        CurrentTaskCancelButton.IsEnabled = details.Summary.Status is "Pending" or "Running" or "WaitingForUser";
        CurrentEventsList.ItemsSource = details.Events.Select(EventRow.From).ToArray();
        DecisionCard.Visibility = details.Summary.Status == "WaitingForUser"
            ? Visibility.Visible
            : Visibility.Collapsed;
        DecisionQuestionText.Text = details.PendingDecision?.Question ?? "需要补充信息后才能继续。";
        RenderEvidence(details);
        if (navigate)
        {
            ShowPage(AppPage.CurrentTask);
        }
    }

    private void RenderEvidence(TaskDetailsDto details)
    {
        var evidence = details.Evidence;
        if (evidence is null)
        {
            CurrentVerificationStatusText.Text = "等待证据";
            CurrentUserSummaryText.Text = details.Summary.Status == "Running"
                ? "任务正在执行，完成后会生成可核实的结果。"
                : "当前还没有生成完整的验证证据。";
            SetVerificationColors("Pending");
            EvidenceConflictBanner.Visibility = Visibility.Collapsed;
            EvidenceStatsText.Text = "尚无最终文件和测试证据。";
            ChangedFilesList.ItemsSource = null;
            TestCommandsList.ItemsSource = null;
            AgentFinalExplanationText.Text = "—";
        }
        else
        {
            CurrentVerificationStatusText.Text = VerificationLabel(evidence.VerificationStatus);
            CurrentUserSummaryText.Text = evidence.UserSummary;
            SetVerificationColors(evidence.VerificationStatus);
            EvidenceConflictBanner.Visibility = evidence.AgentClaimContradictedByEvidence
                ? Visibility.Visible
                : Visibility.Collapsed;
            EvidenceStatsText.Text =
                $"文件：新增 {evidence.AddedFileCount}，修改 {evidence.ModifiedFileCount}，删除 {evidence.DeletedFileCount}\n" +
                $"Diff：+{evidence.AddedLineCount?.ToString() ?? "?"} / -{evidence.DeletedLineCount?.ToString() ?? "?"}\n" +
                $"测试：{TestStatusLabel(evidence.TestStatus)}，通过 {evidence.PassedTests?.ToString() ?? "?"}，失败 {evidence.FailedTests?.ToString() ?? "?"}";
            ChangedFilesList.ItemsSource = evidence.ChangedFiles.Select(FileRow.From).ToArray();
            TestCommandsList.ItemsSource = evidence.TestCommands.Select(TestRow.From).ToArray();
            AgentFinalExplanationText.Text = string.IsNullOrWhiteSpace(evidence.AgentFinalExplanation)
                ? "编程助手没有提供最终说明。"
                : evidence.AgentFinalExplanation;
        }

        TaskTechnicalInfoText.Text =
            $"运行标识: {details.ThreadId ?? "—"}\n" +
            $"Attempt: {details.CurrentAttempt}\n" +
            $"Connector: {details.ConnectorVersion ?? "—"}\n" +
            $"Working directory: {details.WorkingDirectoryRelativePath}";
    }

    private void SetVerificationColors(string status)
    {
        var (background, border) = status switch
        {
            "Verified" => ("#EAF6F0", "#79B79C"),
            "Unverified" => ("#FFF7E6", "#E4C47C"),
            "VerificationFailed" or "Failed" => ("#FCECED", "#E3A1A5"),
            "Cancelled" => ("#F0F2F2", "#B7C0C0"),
            "Interrupted" => ("#FFF3E5", "#D9A263"),
            _ => ("#EEF3F5", "#B8CBD3")
        };
        CurrentVerificationBanner.Background = BrushFrom(background);
        CurrentVerificationBanner.BorderBrush = BrushFrom(border);
    }

    private async Task CreateTaskAsync(ProjectDto? project, string instruction, string? title)
    {
        if (project is null)
        {
            ShowError("请先选择一个已授权项目。", null);
            return;
        }

        if (string.IsNullOrWhiteSpace(instruction))
        {
            ShowError("请描述希望电脑完成什么。", null);
            return;
        }

        await RunCommandAsync(async () =>
        {
            var result = await _api.CreateTaskAsync(
                new CreateTaskRequestDto(project.Id, instruction, title),
                _lifetime.Token);
            _selectedTaskId = result.TaskId;
            await LoadTaskDetailsAsync(result.TaskId, navigate: true);
            await RefreshAllAsync(showErrors: false);
        });
    }

    private async Task RunCommandAsync(
        Func<Task> operation,
        CancellationToken operationToken = default)
    {
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            await operation();
            ErrorBanner.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (
            _lifetime.IsCancellationRequested || operationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var error = UserFacingErrorMapper.Map(exception);
            ShowError($"{error.Message} {error.SuggestedAction}", exception);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private async Task SubmitVoiceCommandAsync(
        string text,
        CancellationToken cancellationToken)
    {
        await RunCommandAsync(async () =>
        {
            var plan = await _api.PlanAssistantCommandAsync(
                new PlanAssistantCommandRequestDto(
                    text,
                    InputModality: "Voice"),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _pendingAssistantPlan = plan;
            var ready = string.Equals(plan.Readiness, "Ready", StringComparison.Ordinal);
            var directVoiceAction = ready
                                    && IsDirectVoiceAction(plan.IntentKind);
            if (ready && (!plan.RequiresConfirmation || directVoiceAction))
            {
                AssistantPlanBorder.Visibility = Visibility.Collapsed;
                AssistantResultBorder.Visibility = Visibility.Visible;
                AssistantResultStatusText.Text = directVoiceAction
                    ? "正在执行语音指令"
                    : "正在回答";
                AssistantResultText.Text = plan.UserSummary;
                StatusBarText.Text = plan.UserSummary;
                await ExecutePendingAssistantPlanAsync(
                    confirmed: false,
                    authorizationSource: directVoiceAction
                        ? "ExplicitVoice"
                        : "VisibleConfirmation",
                    cancellationToken);
                return;
            }

            RenderAssistantPlan(plan);
        }, cancellationToken);
    }

    private static bool IsDirectVoiceAction(string intentKind) =>
        intentKind is "OpenApplication" or "OpenWebsite" or "SearchForeground";

    private void RenderAssistantPlan(AssistantIntentPlanDto plan)
    {
        AssistantPlanBorder.Visibility = Visibility.Visible;
        AssistantPlanSummaryText.Text = plan.UserSummary;
        var foreground = plan.ForegroundApplication is null
            ? string.Empty
            : $"\n目标窗口：{plan.ForegroundApplication.WindowTitle}";
        AssistantPlanDetailText.Text = plan.Readiness switch
        {
            "NeedsContext" => $"还需要：{plan.MissingContext}{foreground}",
            "Unsupported" => "当前版本不能安全执行这条请求。",
            _ => $"{plan.ConfirmationText ?? "这条请求不会操作电脑。"}{foreground}"
        };
        ConfirmAssistantPlanButton.Visibility =
            string.Equals(plan.Readiness, "Ready", StringComparison.Ordinal)
                ? Visibility.Visible
                : Visibility.Collapsed;
        ConfirmAssistantPlanButton.Content = plan.RequiresConfirmation ? "确认这一次" : "开始回答";
    }

    private async void ConfirmAssistantPlanButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(() => ExecutePendingAssistantPlanAsync(
            confirmed: true,
            authorizationSource: "VisibleConfirmation"));

    private async Task ExecutePendingAssistantPlanAsync(
        bool confirmed,
        string authorizationSource,
        CancellationToken commandCancellationToken = default)
    {
        var plan = _pendingAssistantPlan
            ?? throw new InvalidOperationException("这条操作计划已经失效，请重新说一次。 ");
        var isWindowObservation = string.Equals(
            plan.IntentKind,
            "DescribeForeground",
            StringComparison.Ordinal);
        CancellationToken executionToken = commandCancellationToken.CanBeCanceled
            ? commandCancellationToken
            : _lifetime.Token;
        if (isWindowObservation)
        {
            _windowObservationCancellation?.Dispose();
            _windowObservationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token,
                executionToken);
            executionToken = _windowObservationCancellation.Token;
            WindowObservationTargetText.Text = plan.ForegroundApplication is null
                ? "只读取本次确认的单个窗口；图像不保存、不上传。"
                : $"目标：{plan.ForegroundApplication.WindowTitle}。图像不保存、不上传。";
            WindowObservationBorder.Visibility = Visibility.Visible;
        }

        var operationId = $"assistant-ui-{Guid.NewGuid():N}";
        if (isWindowObservation)
        {
            _activeWindowObservationId = operationId;
        }

        AssistantCommandResultDto result;
        try
        {
            result = await _api.ExecuteAssistantCommandAsync(
                new ExecuteAssistantCommandRequestDto(
                    plan.PlanId,
                    confirmed,
                    operationId,
                    authorizationSource),
                executionToken);
            executionToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (
            _windowObservationCancellation?.IsCancellationRequested == true
            && !_lifetime.IsCancellationRequested)
        {
            _pendingAssistantPlan = null;
            AssistantPlanBorder.Visibility = Visibility.Collapsed;
            AssistantResultBorder.Visibility = Visibility.Visible;
            AssistantResultStatusText.Text = "已停止查看";
            AssistantResultText.Text = "本次窗口读取已停止，捕获到的临时画面已从内存清理。";
            StatusBarText.Text = "已停止查看窗口。";
            return;
        }
        finally
        {
            if (isWindowObservation)
            {
                WindowObservationBorder.Visibility = Visibility.Collapsed;
                _windowObservationCancellation?.Dispose();
                _windowObservationCancellation = null;
                _activeWindowObservationId = null;
            }
        }
        _pendingAssistantPlan = null;
        AssistantPlanBorder.Visibility = Visibility.Collapsed;
        AssistantResultBorder.Visibility = Visibility.Visible;
        AssistantResultStatusText.Text = result.VerificationStatus switch
        {
            "ExecutionVerified" => "本次操作已核实执行",
            "ObservationCompleted" => "已完成本次窗口识别",
            "LaunchRequested" => "Windows 已接受启动请求",
            "Pending" => "任务执行中",
            _ => "元枢正在处理"
        };
        AssistantResultText.Text = result.Evidence is null
            ? result.UserSummary
            : string.Join("\n", new[]
            {
                result.UserSummary,
                result.Evidence.VerifiedFacts.Count == 0
                    ? string.Empty
                    : "已核实：" + string.Join("；", result.Evidence.VerifiedFacts),
                result.Evidence.UnverifiedFacts.Count == 0
                    ? string.Empty
                    : "尚未核实：" + string.Join("；", result.Evidence.UnverifiedFacts)
            }.Where(line => !string.IsNullOrWhiteSpace(line)));
        AssistantResultBorder.Background = BrushFrom(
            result.Status == "Failed" ? "#FCECED" : "#F7FAFB");
        if (result.TaskId is { } taskId)
        {
            _selectedTaskId = taskId;
        }

        if (result.ConversationId is { } conversationId)
        {
            _assistantConversationId = conversationId;
        }

        StatusBarText.Text = result.UserSummary;
        await RefreshAllAsync(showErrors: false);
    }

    private async void StopWindowObservationButton_Click(object sender, RoutedEventArgs e)
    {
        _windowObservationCancellation?.Cancel();
        WindowObservationTargetText.Text = "正在停止并清理临时画面…";
        var operationId = _activeWindowObservationId;
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return;
        }

        try
        {
            await _api.CancelWindowObservationAsync(operationId, _lifetime.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError("停止请求没有成功送达本机中枢，请稍后再试。", exception);
        }
    }

    private void CancelAssistantPlanButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingAssistantPlan = null;
        AssistantPlanBorder.Visibility = Visibility.Collapsed;
        StatusBarText.Text = "已取消，不会执行任何电脑操作。";
    }

    private async Task StartContinuousVoiceAsync()
    {
        var report = _voice.GetCapabilityReport();
        if (!report.RecognitionModelAvailable || report.MicrophoneCount == 0)
        {
            RenderVoiceStatus();
            return;
        }

        try
        {
            await Task.Run(() => _voice.StartAsync(_lifetime.Token), _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RenderVoiceState(
                ContinuousVoiceState.Faulted,
                UserFacingErrorMapper.Map(exception).Message);
        }
    }

    private void Voice_PartialTextChanged(object? sender, string text) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (_voice.IsListening && !string.IsNullOrWhiteSpace(text))
            {
                VoiceTranscriptText.Text = text;
                if (SpeechEchoMatcher.CanInterrupt(text) && !IsAssistantSpeechEcho(text))
                {
                    _speechCancellation?.Cancel();
                    _activeVoiceCommandCancellation?.Cancel();
                    VoiceStateText.Text = "听到你插话，正在停止上一条";
                }
            }
        });

    private void Voice_UtteranceRecognized(object? sender, VoiceUtterance utterance) =>
        Dispatcher.BeginInvoke(() => QueueVoiceUtterance(utterance));

    private void QueueVoiceUtterance(VoiceUtterance utterance)
    {
        var text = utterance.Text.Trim();
        if (text.Length == 0 || IsAssistantSpeechEcho(text))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (string.Equals(text, _lastVoiceUtterance, StringComparison.Ordinal)
            && now - _lastVoiceUtteranceAt < TimeSpan.FromSeconds(3))
        {
            return;
        }

        _lastVoiceUtterance = text;
        _lastVoiceUtteranceAt = now;
        VoiceTranscriptText.Text = text;
        AssistantPlanBorder.Visibility = Visibility.Collapsed;
        AssistantResultBorder.Visibility = Visibility.Collapsed;
        ErrorBanner.Visibility = Visibility.Collapsed;
        _pendingAssistantPlan = null;
        _queuedVoiceUtterance = utterance with { Text = text };
        _speechCancellation?.Cancel();
        _activeVoiceCommandCancellation?.Cancel();
        if (_voiceCommandBusy)
        {
            VoiceStateText.Text = "已收到新指令，正在停止上一条";
            StatusBarText.Text = $"新指令优先：{text}";
            return;
        }

        _ = ProcessVoiceQueueAsync();
    }

    private async Task ProcessVoiceQueueAsync()
    {
        _voiceCommandBusy = true;
        try
        {
            while (_queuedVoiceUtterance is { } utterance)
            {
                _queuedVoiceUtterance = null;
                _activeVoiceCommandCancellation?.Dispose();
                _activeVoiceCommandCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                var cancellationToken = _activeVoiceCommandCancellation.Token;
                try
                {
                    RenderVoiceState(ContinuousVoiceState.HearingSpeech, "已听懂，正在处理");
                    StatusBarText.Text = $"已听到：{utterance.Text}";
                    await SubmitVoiceCommandAsync(utterance.Text, cancellationToken);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested
                    && !_lifetime.IsCancellationRequested)
                {
                    StatusBarText.Text = _queuedVoiceUtterance is null
                        ? "上一条指令已停止。"
                        : "上一条指令已停止，正在处理你的新指令。";
                }
            }
        }
        finally
        {
            _activeVoiceCommandCancellation?.Dispose();
            _activeVoiceCommandCancellation = null;
            _voiceCommandBusy = false;
            if (_voice.IsListening)
            {
                RenderVoiceState(ContinuousVoiceState.Listening, "正在聆听");
            }
        }
    }

    private bool IsAssistantSpeechEcho(string text)
    {
        var reference = _activeSpeechText;
        if (string.IsNullOrWhiteSpace(reference)
            && DateTimeOffset.UtcNow - _lastAssistantSpeechEndedAt < TimeSpan.FromSeconds(3))
        {
            reference = _lastAssistantSpeechText;
        }

        return !string.IsNullOrWhiteSpace(reference)
               && SpeechEchoMatcher.IsLikelyAssistantEcho(text, reference);
    }

    private void Voice_StateChanged(object? sender, ContinuousVoiceStatus status) =>
        Dispatcher.BeginInvoke(() => RenderVoiceState(status.State, status.Message));

    private void RenderVoiceState(ContinuousVoiceState state, string message)
    {
        VoiceStateText.Text = message;
        DashboardCodexVersionText.Text = state switch
        {
            ContinuousVoiceState.Listening => "自动聆听中",
            ContinuousVoiceState.HearingSpeech => "正在识别",
            ContinuousVoiceState.Faulted => "需要检查",
            _ => "未启动"
        };
        var color = state switch
        {
            ContinuousVoiceState.Listening => "#1F8A6A",
            ContinuousVoiceState.HearingSpeech => "#2E6DD8",
            ContinuousVoiceState.Faulted => "#C94C4C",
            _ => "#A56210"
        };
        VoiceStateDot.Background = BrushFrom(color);
        DashboardCodexVersionText.Foreground = BrushFrom(color);
    }

    private void RenderVoiceStatus()
    {
        var report = _voice.GetCapabilityReport();
        if (report.RecognitionModelAvailable && report.MicrophoneCount > 0)
        {
            RenderVoiceState(
                _voice.IsListening ? ContinuousVoiceState.Listening : ContinuousVoiceState.Stopped,
                _voice.IsListening ? "正在聆听" : "正在启动语音中枢");
            return;
        }

        RenderVoiceState(
            ContinuousVoiceState.Faulted,
            report.RecognitionModelAvailable ? "没有检测到麦克风" : "需要安装本地语音模型");
        VoicePrivacyText.Text = report.Message;
    }

    private async Task RefreshAssistantConversationAsync(Guid conversationId)
    {
        var details = await _api.GetConversationAsync(conversationId, _lifetime.Token);
        if (details is null)
        {
            return;
        }

        var assistantMessage = details.Messages.LastOrDefault(message =>
            string.Equals(message.Role, "Assistant", StringComparison.OrdinalIgnoreCase));
        AssistantResultBorder.Visibility = Visibility.Visible;
        if (assistantMessage is not null)
        {
            AssistantResultStatusText.Text = "元枢回答";
            AssistantResultText.Text = assistantMessage.Content;
        }
        else
        {
            AssistantResultStatusText.Text = "正在思考";
            AssistantResultText.Text = details.Summary.FailureMessage ?? "正在准备回答…";
        }
    }

    private async void ReadAssistantResultButton_Click(object sender, RoutedEventArgs e)
    {
        var text = AssistantResultText.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await RunCommandAsync(async () =>
        {
            ReadAssistantResultButton.IsEnabled = false;
            _speechCancellation?.Cancel();
            _speechCancellation?.Dispose();
            _speechCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _activeSpeechText = text;
            try
            {
                await _speechOutput.SpeakAsync(text, _speechCancellation.Token);
            }
            catch (OperationCanceledException) when (_speechCancellation.IsCancellationRequested)
            {
                StatusBarText.Text = "已听到你插话，元枢已停止朗读。";
            }
            finally
            {
                _lastAssistantSpeechText = text;
                _lastAssistantSpeechEndedAt = DateTimeOffset.UtcNow;
                _activeSpeechText = null;
                _speechCancellation.Dispose();
                _speechCancellation = null;
                ReadAssistantResultButton.IsEnabled = true;
            }
        });
    }

    private async void NewTaskSubmitButton_Click(object sender, RoutedEventArgs e)
    {
        await CreateTaskAsync(
            NewTaskProjectComboBox.SelectedItem as ProjectDto,
            NewTaskInstructionTextBox.Text,
            NewTaskTitleTextBox.Text);
        NewTaskInstructionTextBox.Clear();
        NewTaskTitleTextBox.Clear();
    }

    private void NewTaskProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        TaskDirectoryNoticeText.Text = NewTaskProjectComboBox.SelectedItem is ProjectDto project
            ? $"编程助手将在这个项目目录内执行任务：\n{project.RootPath}"
            : "编程助手将在所选项目目录内执行任务。";
    }

    private async void AddProjectButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要授权给编程助手的 Git 项目",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunCommandAsync(async () =>
        {
            await _api.AddProjectAsync(new AddProjectRequestDto(dialog.FolderName), _lifetime.Token);
            await RefreshAllAsync(showErrors: false);
        });
    }

    private void ProjectsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectsListView.SelectedItem is not ProjectRow project)
        {
            RevokeProjectButton.IsEnabled = false;
            SelectedProjectDetailText.Text = "选择项目查看详情。";
            return;
        }

        SelectedProjectDetailText.Text =
            $"{project.Name}  ·  {project.GitStatus}  ·  {project.RecentTaskCount} 个历史任务";
        RevokeProjectButton.IsEnabled = project.AuthorizationState == "Authorized" && !project.HasActiveTask;
    }

    private async void RevokeProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectsListView.SelectedItem is not ProjectRow project)
        {
            return;
        }

        if (MessageBox.Show(
                $"删除“{project.Name}”的授权？\n\n不会删除项目文件或历史任务。",
                "删除项目授权",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunCommandAsync(async () =>
        {
            await _api.RevokeProjectAsync(project.Id, _lifetime.Token);
            await RefreshAllAsync(showErrors: false);
        });
    }

    private async void CurrentTaskCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTaskId is not { } taskId)
        {
            return;
        }

        if (MessageBox.Show(
                "取消当前任务？编程助手及其子进程会被停止。",
                "取消任务",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunCommandAsync(async () =>
        {
            await _api.CancelTaskAsync(taskId, cancellationToken: _lifetime.Token);
            await LoadTaskDetailsAsync(taskId, navigate: false);
        });
    }

    private async void ContinueTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTaskId is not { } taskId || string.IsNullOrWhiteSpace(DecisionAnswerTextBox.Text))
        {
            ShowError("请输入回答后再继续任务。", null);
            return;
        }

        var answer = DecisionAnswerTextBox.Text;
        await RunCommandAsync(async () =>
        {
            await _api.ContinueTaskAsync(taskId, answer, cancellationToken: _lifetime.Token);
            DecisionAnswerTextBox.Clear();
            await LoadTaskDetailsAsync(taskId, navigate: false);
        });
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "清理所有已经结束的任务历史？\n\n项目文件和正在运行的任务不会被删除。",
                "清理历史记录",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunCommandAsync(async () =>
        {
            var count = await _api.ClearHistoryAsync(_lifetime.Token);
            _selectedTaskId = null;
            MessageBox.Show($"已清理 {count} 条任务历史。", "清理完成");
            await RefreshAllAsync(showErrors: false);
        });
    }

    private async void OpenDesktopApplicationButton_Click(object sender, RoutedEventArgs e)
    {
        if (DesktopApplicationComboBox.SelectedItem is not DesktopApplicationDto application)
        {
            ShowError("请选择一个允许打开的应用。", null);
            return;
        }

        if (MessageBox.Show(
                $"只授权本次操作：打开“{application.DisplayName}”。\n\n不会输入内容、点击控件或执行其他操作。",
                "确认打开应用",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await ExecuteDesktopActionAsync(new ExecuteDesktopActionRequestDto(
            "OpenApplication",
            application.Id,
            true,
            $"desktop-ui-app-{Guid.NewGuid():N}"));
    }

    private async void NewConversationButton_Click(object sender, RoutedEventArgs e)
    {
        await RunCommandAsync(async () =>
        {
            var conversation = await _api.CreateConversationAsync(cancellationToken: _lifetime.Token);
            _selectedConversationId = conversation.Id;
            ConversationInputTextBox.Clear();
            await RefreshAllAsync(showErrors: false);
            await LoadConversationAsync(conversation.Id, navigate: true);
            ConversationInputTextBox.Focus();
        });
    }

    private async void ConversationListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConversationListBox.SelectedItem is ConversationRow row && row.Id != _selectedConversationId)
        {
            await RunCommandAsync(() => LoadConversationAsync(row.Id, navigate: false));
        }
    }

    private async void SendConversationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedConversationId is not { } conversationId)
        {
            ShowError("请先新建一个对话。", null);
            return;
        }

        var message = ConversationInputTextBox.Text;
        if (string.IsNullOrWhiteSpace(message))
        {
            ShowError("请输入想聊的内容。", null);
            return;
        }

        await RunCommandAsync(async () =>
        {
            await _api.SendConversationMessageAsync(
                conversationId,
                message,
                $"desktop-chat-{Guid.NewGuid():N}",
                _lifetime.Token);
            ConversationInputTextBox.Clear();
            await LoadConversationAsync(conversationId, navigate: false);
        });
    }

    private async void StopConversationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedConversationId is not { } conversationId)
        {
            return;
        }

        await RunCommandAsync(async () =>
        {
            await _api.CancelConversationTurnAsync(conversationId, _lifetime.Token);
            StatusBarText.Text = "正在停止回答。";
            await Task.Delay(150, _lifetime.Token);
            await LoadConversationAsync(conversationId, navigate: false);
        });
    }

    private void ConversationOpenTaskButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(AppPage.NewTask);

    private void ConversationOpenActionsButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(AppPage.DesktopActions);

    private async void OpenDesktopWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        var website = DesktopWebsiteTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(website))
        {
            ShowError("请输入要打开的 https 网站地址。", null);
            return;
        }

        if (MessageBox.Show(
                $"只授权本次操作：用默认浏览器打开下面的网站。\n\n{website}\n\n请确认地址正确。",
                "确认打开网站",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await ExecuteDesktopActionAsync(new ExecuteDesktopActionRequestDto(
            "OpenWebsite",
            website,
            true,
            $"desktop-ui-website-{Guid.NewGuid():N}"));
    }

    private async Task ExecuteDesktopActionAsync(ExecuteDesktopActionRequestDto request)
    {
        await RunCommandAsync(async () =>
        {
            var result = await _api.ExecuteDesktopActionAsync(request, _lifetime.Token);
            DesktopActionResultText.Text = result.Message;
            DesktopActionResultBorder.Background = BrushFrom(result.Succeeded ? "#EAF6F0" : "#FCECED");
            DesktopActionResultBorder.BorderBrush = BrushFrom(result.Succeeded ? "#79B79C" : "#E3A1A5");
            DesktopActionResultBorder.Visibility = Visibility.Visible;
            StatusBarText.Text = result.Message;
        });
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var runInBackground = RunInBackgroundCheckBox.IsChecked == true;
        _settings = _settings with
        {
            StartWithWindows = StartWithWindowsCheckBox.IsChecked == true,
            RunInBackground = runInBackground,
            CloseToTray = runInBackground && CloseToTrayCheckBox.IsChecked == true,
            NotificationsEnabled = NotificationsEnabledCheckBox.IsChecked == true
        };
        await RunCommandAsync(async () =>
        {
            _startupService.SetEnabled(_settings.StartWithWindows);
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
            StatusBarText.Text = "设置已保存。";
        });
    }

    public void OpenDashboardFromTray() => ShowWindowAndPage(AppPage.Dashboard);

    public void OpenNewTaskFromTray() => ShowWindowAndPage(AppPage.Dashboard);

    public void OpenCurrentTaskFromTray() => ShowWindowAndPage(AppPage.CurrentTask);

    public void OpenSettingsFromTray() => ShowWindowAndPage(AppPage.Settings);

    public async Task OpenTaskFromNotificationAsync(Guid taskId)
    {
        ShowWindowAndPage(AppPage.CurrentTask);
        await RunCommandAsync(() => LoadTaskDetailsAsync(taskId, navigate: false));
    }

    public void OpenConversationCenterFromOnboarding()
        => ShowWindowAndPage(AppPage.Conversations);

    public async Task CancelCurrentTaskFromTrayAsync()
    {
        if (_selectedTaskId is not { } taskId)
        {
            ShowWindowAndPage(AppPage.CurrentTask);
            return;
        }

        await RunCommandAsync(async () =>
        {
            await _api.CancelTaskAsync(taskId, cancellationToken: _lifetime.Token);
            await LoadTaskDetailsAsync(taskId, navigate: false);
        });
    }

    public async Task ExitApplicationAsync()
    {
        _forceClose = true;
        try
        {
            await _api.ShutdownHostAsync(CancellationToken.None);
        }
        catch
        {
        }

        System.Windows.Application.Current.Shutdown();
    }

    private void ShowWindowAndPage(AppPage page)
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        ShowPage(page);
    }

    private void OpenDataDirectoryButton_Click(object sender, RoutedEventArgs e) =>
        OpenDirectory(_systemStatus?.DataDirectory);

    private void OpenLogsDirectoryButton_Click(object sender, RoutedEventArgs e) =>
        OpenDirectory(_systemStatus?.LogsDirectory);

    private static void OpenDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private void HistoryFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            RenderHistory();
        }
    }

    private async void TaskList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var taskId = sender switch
        {
            ListBox listBox when listBox.SelectedItem is TaskRow row => row.Id,
            ListView listView when listView.SelectedItem is HistoryRow row => row.Id,
            _ => Guid.Empty
        };
        if (taskId != Guid.Empty)
        {
            await RunCommandAsync(() => LoadTaskDetailsAsync(taskId, navigate: true));
        }
    }

    private void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string page }
            && Enum.TryParse<AppPage>(page, out var parsed))
        {
            ShowPage(parsed);
        }
    }

    private void OpenNewTaskButton_Click(object sender, RoutedEventArgs e) => ShowPage(AppPage.NewTask);

    private void ShowPage(AppPage page)
    {
        DashboardPage.Visibility = page == AppPage.Dashboard ? Visibility.Visible : Visibility.Collapsed;
        ConversationsPage.Visibility = page == AppPage.Conversations ? Visibility.Visible : Visibility.Collapsed;
        ProjectsPage.Visibility = page == AppPage.Projects ? Visibility.Visible : Visibility.Collapsed;
        DesktopActionsPage.Visibility = page == AppPage.DesktopActions ? Visibility.Visible : Visibility.Collapsed;
        NewTaskPage.Visibility = page == AppPage.NewTask ? Visibility.Visible : Visibility.Collapsed;
        CurrentTaskPage.Visibility = page == AppPage.CurrentTask ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == AppPage.History ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == AppPage.Settings ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[]
                 {
                     DashboardNavButton, ConversationsNavButton, ProjectsNavButton, DesktopActionsNavButton, NewTaskNavButton,
                     CurrentTaskNavButton, HistoryNavButton, SettingsNavButton
                 })
        {
            button.Tag = null;
        }

        var selected = page switch
        {
            AppPage.Dashboard => DashboardNavButton,
            AppPage.Conversations => ConversationsNavButton,
            AppPage.Projects => ProjectsNavButton,
            AppPage.DesktopActions => DesktopActionsNavButton,
            AppPage.NewTask => NewTaskNavButton,
            AppPage.CurrentTask => CurrentTaskNavButton,
            AppPage.History => HistoryNavButton,
            _ => SettingsNavButton
        };
        selected.Tag = "Selected";
        PageTitleText.Text = page switch
        {
            AppPage.Dashboard => "首页",
            AppPage.Conversations => "对话",
            AppPage.Projects => "项目",
            AppPage.DesktopActions => "电脑操作",
            AppPage.NewTask => "编程任务",
            AppPage.CurrentTask => "当前任务",
            AppPage.History => "任务历史",
            _ => "设置"
        };
    }

    private void SetHostOnline(bool online)
    {
        _isHostOnline = online;
        HostStatusText.Text = online ? "本机中枢在线" : "本机中枢离线";
        HostStatusPill.Background = BrushFrom(online ? "#EAF6F0" : "#FCECED");
        HostStatusText.Foreground = BrushFrom(online ? "#1F8A6A" : "#C94C4C");
    }

    private void ShowError(string message, Exception? exception)
    {
        ErrorMessageText.Text = message;
        ErrorTechnicalText.Text = exception is null
            ? "没有更多技术信息。"
            : UserFacingErrorMapper.Map(exception).TechnicalDetail;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void CloseErrorButton_Click(object sender, RoutedEventArgs e) =>
        ErrorBanner.Visibility = Visibility.Collapsed;

    private static string StatusLabel(string status) => status switch
    {
        "Pending" => "准备中",
        "Running" => "正在执行",
        "WaitingForUser" => "等待你决定",
        "CancellationRequested" => "正在取消",
        "Succeeded" => "执行完成",
        "Failed" => "执行失败",
        "Cancelled" => "已取消",
        "Interrupted" => "意外中断",
        _ => status
    };

    private static string VerificationLabel(string status) => status switch
    {
        "Verified" => "系统已验证",
        "Unverified" => "尚未验证",
        "VerificationFailed" => "验证未通过",
        "Cancelled" => "任务已取消",
        "Failed" => "任务失败",
        "Interrupted" => "任务已中断",
        _ => "等待证据"
    };

    private static string TestStatusLabel(string status) => status switch
    {
        "Passed" => "测试通过",
        "Failed" => "测试失败",
        "NotRun" => "没有测试证据",
        "Incomplete" => "测试证据不完整",
        _ => status
    };

    private static string ConversationStatusLabel(string status, string? failureMessage) => status switch
    {
        "Ready" => "可以继续聊天",
        "Responding" => "元枢正在回答…",
        "Failed" => string.IsNullOrWhiteSpace(failureMessage) ? "上次回答失败" : failureMessage,
        "Interrupted" => "上次回答意外中断，可以继续发送消息",
        _ => status
    };

    private static string DurationText(TaskSummaryDto task)
    {
        if (task.StartedAtUtc is null)
        {
            return "尚未开始";
        }

        var end = task.CompletedAtUtc ?? DateTimeOffset.UtcNow;
        var duration = end - task.StartedAtUtc.Value;
        return duration.TotalHours >= 1
            ? $"已用 {duration.TotalHours:0.#} 小时"
            : $"已用 {Math.Max(0, duration.TotalMinutes):0} 分钟";
    }

    private static SolidColorBrush BrushFrom(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private enum AppPage
    {
        Dashboard,
        Conversations,
        Projects,
        DesktopActions,
        NewTask,
        CurrentTask,
        History,
        Settings
    }

    private sealed record TaskRow(Guid Id, string Text)
    {
        public static TaskRow From(TaskSummaryDto task) =>
            new(task.Id, $"{task.Title}  ·  {StatusLabel(task.Status)}");

        public override string ToString() => Text;
    }

    private sealed record ConversationRow(Guid Id, string Title, string StatusText)
    {
        public static ConversationRow From(ConversationSummaryDto conversation) => new(
            conversation.Id,
            conversation.Title,
            ConversationStatusLabel(conversation.Status, conversation.FailureMessage));
    }

    private sealed record ConversationMessageRow(
        string RoleText,
        string Content,
        string TimeText,
        Brush Background,
        Brush BorderBrush,
        HorizontalAlignment Alignment,
        Thickness Margin)
    {
        public static ConversationMessageRow From(ConversationMessageDto message)
        {
            var isUser = message.Role == "User";
            return new ConversationMessageRow(
                isUser ? "你" : "元枢",
                message.Content,
                message.CreatedAtUtc.ToLocalTime().ToString("HH:mm"),
                BrushFrom(isUser ? "#EAF0FF" : "#F7FAFB"),
                BrushFrom(isUser ? "#BCD0F6" : "#DDE5E8"),
                isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                isUser ? new Thickness(90, 5, 0, 5) : new Thickness(0, 5, 90, 5));
        }
    }

    private sealed record ProjectRow(
        Guid Id,
        string Name,
        string RootPath,
        string GitStatus,
        string AuthorizationState,
        int RecentTaskCount,
        bool HasActiveTask,
        string ActiveText)
    {
        public static ProjectRow From(ProjectDto project) => new(
            project.Id,
            project.Name,
            project.RootPath,
            project.GitStatus,
            project.AuthorizationState,
            project.RecentTaskCount,
            project.HasActiveTask,
            project.HasActiveTask ? "是" : "否");
    }

    private sealed record HistoryRow(
        Guid Id,
        string Title,
        string ProjectName,
        string Status,
        string VerificationStatus,
        string TimeText,
        string UserSummary)
    {
        public static HistoryRow From(TaskSummaryDto task) => new(
            task.Id,
            task.Title,
            task.ProjectName,
            StatusLabel(task.Status),
            string.IsNullOrWhiteSpace(task.VerificationStatus)
                ? "等待证据"
                : VerificationLabel(task.VerificationStatus),
            task.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            task.UserSummary ?? "尚无结果摘要");
    }

    private sealed record EventRow(string Text)
    {
        public static EventRow From(TaskEventDto item) => new(
            $"{item.OccurredAtUtc.ToLocalTime():HH:mm:ss}  {item.Message}");

        public override string ToString() => Text;
    }

    private sealed record FileRow(string Text)
    {
        public static FileRow From(EvidenceFileDto file) => new(
            $"{file.ChangeType}  {file.RelativePath}  (+{file.AddedLines?.ToString() ?? "?"}/-{file.DeletedLines?.ToString() ?? "?"})");

        public override string ToString() => Text;
    }

    private sealed record TestRow(string Text)
    {
        public static TestRow From(TestCommandDto command) => new(
            $"exit {command.ExitCode?.ToString() ?? "?"}  {command.Command}");

        public override string ToString() => Text;
    }
}
