using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;
using System.Windows;

namespace ScreenGuide.DesktopClient;

public partial class App : System.Windows.Application
{
    private TrayIconService? _tray;
    private DesktopClientSingleInstance? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _singleInstance = DesktopClientSingleInstance.TryAcquire();
        if (_singleInstance is null)
        {
            await DesktopClientSingleInstance.TrySignalExistingAsync();
            Shutdown();
            return;
        }

        try
        {
            DesktopClientFailureRecorder.Clear();
            DesktopClientFailureRecorder.RecordStage("starting-host");
            IDesktopApiClient apiClient = new DesktopApiClient();
            var hostProcessManager = new DesktopHostProcessManager(apiClient);
            await hostProcessManager.EnsureRunningAsync();
            DesktopClientFailureRecorder.RecordStage("loading-settings");
            var settingsStore = new DesktopClientSettingsStore();
            var settings = await settingsStore.LoadAsync();
            DesktopClientFailureRecorder.RecordStage($"settings-loaded:onboarding={settings.OnboardingCompleted}");
            Guid? onboardingTaskId = null;
            if (!settings.OnboardingCompleted)
            {
                var onboarding = new OnboardingWindow(apiClient, settingsStore, settings);
                if (onboarding.ShowDialog() != true || !onboarding.Completed)
                {
                    Shutdown();
                    return;
                }

                onboardingTaskId = onboarding.CreatedTaskId;
                settings = await settingsStore.LoadAsync();
            }

            var startupService = new WindowsStartupService();
            DesktopClientFailureRecorder.RecordStage("creating-tray");
            _tray = new TrayIconService();
            var window = new MainWindow(
                apiClient,
                hostProcessManager,
                settingsStore,
                startupService,
                _tray,
                settings);
            DesktopClientFailureRecorder.RecordStage("main-window-created");
            MainWindow = window;
            _singleInstance.SetActivationHandler(() => Dispatcher.Invoke(window.OpenDashboardFromTray));
            _tray.OpenRequested += window.OpenDashboardFromTray;
            _tray.NewTaskRequested += window.OpenNewTaskFromTray;
            _tray.CurrentTaskRequested += window.OpenCurrentTaskFromTray;
            _tray.CancelCurrentTaskRequested += async () => await window.CancelCurrentTaskFromTrayAsync();
            _tray.SettingsRequested += window.OpenSettingsFromTray;
            _tray.NotificationClicked += async taskId => await window.OpenTaskFromNotificationAsync(taskId);
            _tray.ExitRequested += async () => await window.ExitApplicationAsync();
            var backgroundStart = e.Args.Any(argument =>
                string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
            var forceShow = e.Args.Any(argument =>
                string.Equals(argument, "--show", StringComparison.OrdinalIgnoreCase));
            if (forceShow || !backgroundStart || !settings.RunInBackground)
            {
                window.Show();
                DesktopClientFailureRecorder.RecordStage("main-window-shown");
            }

            if (onboardingTaskId is { } taskId)
            {
                window.Show();
                await window.OpenTaskFromNotificationAsync(taskId);
            }
        }
        catch (Exception exception)
        {
            DesktopClientFailureRecorder.Record(exception);
            MessageBox.Show(
                $"本地任务服务启动失败。\n\n{exception.Message}",
                "元枢本地任务",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

}
