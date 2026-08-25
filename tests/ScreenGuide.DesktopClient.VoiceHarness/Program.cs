using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ScreenGuide.AI.Core;
using ScreenGuide.DesktopClient;
using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Voice.Windows;

namespace ScreenGuide.DesktopClient.VoiceHarness;

internal static class Program
{
    private const string ResultPrefix = "SCREEN_GUIDE_VOICE_HARNESS_RESULT ";
    private const string OldInput = "VOICE_HARNESS_BLOCK_THEN_LATE_RESULT";
    private const string NewInput = "请回答独立语音替换问题。";
    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web);

    [STAThread]
    private static int Main(string[] args)
    {
        var stopwatch = Stopwatch.StartNew();
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"screen-guide-voice-harness-{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(testRoot, "user-data");
        App? application = null;
        IHost? host = null;
        TrayIconService? tray = null;
        ControlledVoiceListener? voice = null;
        Task<HarnessEvidence>? scenario = null;
        HarnessEvidence? evidence = null;
        Exception? failure = null;
        var errorCode = "voice_harness_unexpected";
        var entryAssemblyConfirmed = false;
        var resourceAssemblyConfirmed = false;
        var naturalShutdown = false;
        var cleanupCompleted = false;

        try
        {
            Require(args.Length == 1 && File.Exists(args[0]), "fake_codex_missing");
            Directory.CreateDirectory(dataRoot);
            application = new App
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            var harnessAssembly = typeof(Program).Assembly;
            entryAssemblyConfirmed = ReferenceEquals(Assembly.GetEntryAssembly(), harnessAssembly);
            resourceAssemblyConfirmed = ReferenceEquals(Application.ResourceAssembly, harnessAssembly);
            Require(entryAssemblyConfirmed, "entry_assembly_invalid");
            Require(resourceAssemblyConfirmed, "resource_assembly_invalid");
            application.InitializeComponent();

            var settingsStore = new DesktopClientSettingsStore(dataRoot);
            var clientSettings = new DesktopClientSettings
            {
                RunInBackground = false,
                CloseToTray = false,
                NotificationsEnabled = false,
                OnboardingCompleted = true,
                NotificationStateInitialized = true
            };
            settingsStore.SaveAsync(clientSettings).GetAwaiter().GetResult();

            var provider = RecordingChatProvider.Create();
            var pipeName = $"ScreenGuide.VoiceHarness.{Guid.NewGuid():N}";
            host = DesktopHostFactory.Build(
                [],
                new DesktopHostOptions(dataRoot, args[0], pipeName),
                services =>
                {
                    services.RemoveAll<IChatModelProvider>();
                    services.AddSingleton<IChatModelProvider>(provider);
                });
            host.StartAsync().GetAwaiter().GetResult();

            var api = new DesktopApiClient(pipeName, TimeSpan.FromSeconds(5));
            WaitForHostAsync(api, TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            voice = new ControlledVoiceListener();
            tray = new TrayIconService();
            var window = new MainWindow(
                api,
                new DesktopHostProcessManager(api),
                settingsStore,
                new WindowsStartupService(),
                tray,
                clientSettings,
                voice);
            application.MainWindow = window;
            var shutdownOutcome = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            scenario = Task.Run(async () =>
            {
                try
                {
                    return await RunScenarioAsync(
                        application,
                        window,
                        api,
                        voice,
                        provider).ConfigureAwait(false);
                }
                finally
                {
                    shutdownOutcome.TrySetResult(await RequestNaturalShutdownAsync(
                        application,
                        window).ConfigureAwait(false));
                }
            });

            window.Show();
            Dispatcher.Run();
            evidence = scenario.WaitAsync(TimeSpan.FromSeconds(90)).GetAwaiter().GetResult();
            naturalShutdown = shutdownOutcome.Task
                .WaitAsync(TimeSpan.FromSeconds(10))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            failure = Unwrap(exception);
            errorCode = failure is HarnessFailureException harnessFailure
                ? harnessFailure.Code
                : "voice_harness_unexpected";
            if (application is not null
                && !application.Dispatcher.HasShutdownStarted
                && !application.Dispatcher.HasShutdownFinished)
            {
                application.Shutdown(-1);
            }
        }
        finally
        {
            try
            {
                host?.StopAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                host?.Dispose();
                tray?.Dispose();
                if (voice is not null)
                {
                    voice.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                if (Directory.Exists(testRoot))
                {
                    Directory.Delete(testRoot, recursive: true);
                }

                cleanupCompleted = true;
            }
            catch (Exception cleanupException)
            {
                failure ??= Unwrap(cleanupException);
                errorCode = "voice_harness_cleanup_failed";
            }
        }

        stopwatch.Stop();
        if (failure is null && evidence is not null && naturalShutdown && cleanupCompleted)
        {
            var result = HarnessResult.Completed(
                evidence,
                entryAssemblyConfirmed,
                resourceAssemblyConfirmed,
                stopwatch.ElapsedMilliseconds);
            Console.Out.WriteLine(ResultPrefix + JsonSerializer.Serialize(result, ResultJson));
            return 0;
        }

        var failed = HarnessResult.Failed(
            entryAssemblyConfirmed,
            resourceAssemblyConfirmed,
            naturalShutdown && cleanupCompleted,
            stopwatch.ElapsedMilliseconds,
            errorCode,
            failure?.GetType().Name ?? "HarnessFailureException");
        Console.Out.WriteLine(ResultPrefix + JsonSerializer.Serialize(failed, ResultJson));
        return 1;
    }

    private static async Task<HarnessEvidence> RunScenarioAsync(
        Application application,
        MainWindow window,
        DesktopApiClient api,
        ControlledVoiceListener voice,
        RecordingChatProvider provider)
    {
        await voice.Started.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var windowHandle = await application.Dispatcher
            .InvokeAsync(() => new WindowInteropHelper(window).Handle)
            .Task
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        Require(windowHandle != IntPtr.Zero, "window_handle_missing");
        var automationRoot = AutomationElement.FromHandle(windowHandle);
        Require(automationRoot is not null, "automation_root_missing");

        voice.Recognize(OldInput);
        var oldTurnId = await provider.BlockingTurnStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        var respondingSnapshot = await WaitForSessionAsync(
            api,
            snapshot => snapshot.Turns.Any(turn =>
                turn.Id == oldTurnId && turn.Phase == "Responding"),
            TimeSpan.FromSeconds(15),
            "old_turn_not_responding").ConfigureAwait(false);
        var oldTurn = respondingSnapshot.Turns.Single(turn => turn.Id == oldTurnId);
        var uiOldInput = await WaitForElementByNameAsync(
            automationRoot!,
            OldInput,
            ControlType.Text,
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await WaitForAutomationTextAsync(
            automationRoot!,
            "SessionStatus",
            "正在回答，你可以随时插话",
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var stop = await WaitForElementByAutomationIdAsync(
            automationRoot!,
            "StopSession",
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var uiShowedStop = stop.Current.IsEnabled && !stop.Current.IsOffscreen;
        Require(uiShowedStop, "stop_state_not_visible");

        voice.Recognize(NewInput);
        var cancellationTurnId = await provider.CancellationObserved.Task
            .WaitAsync(TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        provider.AllowLateReply.TrySetResult();
        var lateReplyTurnId = await provider.LateReplyReturned.Task
            .WaitAsync(TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        var finalSnapshot = await WaitForSessionAsync(
            api,
            snapshot =>
            {
                var prior = snapshot.Turns.SingleOrDefault(turn => turn.Id == oldTurnId);
                var replacement = snapshot.Turns.SingleOrDefault(turn =>
                    turn.Id != oldTurnId && turn.InputText == NewInput);
                return prior?.Phase == "Cancelled" && replacement?.Phase == "Completed";
            },
            TimeSpan.FromSeconds(20),
            "replacement_turn_not_completed").ConfigureAwait(false);
        var finalOldTurn = finalSnapshot.Turns.Single(turn => turn.Id == oldTurnId);
        var newTurn = finalSnapshot.Turns.Single(turn =>
            turn.Id != oldTurnId && turn.InputText == NewInput);
        var newReply = finalSnapshot.Messages.Single(message =>
            message.Role == "Assistant" && message.Content == RecordingChatProvider.NewReply);
        var newInputPersisted = finalSnapshot.Messages.Any(message =>
            message.Role == "User" && message.Content == NewInput);
        var lateReplyRejected = finalSnapshot.Messages.All(message =>
            !message.Content.Contains(RecordingChatProvider.LateReply, StringComparison.Ordinal));
        var uiNewInput = await WaitForElementByNameAsync(
            automationRoot!,
            NewInput,
            ControlType.Text,
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var uiNewReply = await WaitForElementByNameAsync(
            automationRoot!,
            newReply.Content,
            ControlType.Text,
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var uiRejectedLateReply = !ContainsAutomationName(
            automationRoot!,
            RecordingChatProvider.LateReply);

        Require(oldTurn.InputModality == "Voice", "old_input_not_voice");
        Require(newTurn.InputModality == "Voice", "new_input_not_voice");
        Require(newTurn.Id != oldTurnId, "session_turn_not_replaced");
        Require(cancellationTurnId == oldTurnId, "cancellation_turn_mismatch");
        Require(lateReplyTurnId == oldTurnId, "late_reply_turn_mismatch");
        Require(newInputPersisted, "new_input_not_persisted");
        Require(lateReplyRejected, "late_reply_persisted");
        Require(uiRejectedLateReply, "late_reply_visible");

        return new HarnessEvidence(
            finalOldTurn.Phase,
            newTurn.Phase,
            FirstInputVoice: true,
            SecondInputVoice: true,
            DistinctSessionTurns: true,
            OldTurnResponding: true,
            UiShowedOldInput: uiOldInput is not null,
            UiShowedStop: uiShowedStop,
            CancellationObserved: true,
            CancellationTurnMatched: true,
            LateReplyReturned: true,
            NewInputPersisted: newInputPersisted,
            NewReplyPersisted: newReply is not null,
            LateReplyRejected: lateReplyRejected,
            UiShowedNewInput: uiNewInput is not null,
            UiShowedNewReply: uiNewReply is not null,
            UiRejectedLateReply: uiRejectedLateReply);
    }

    private static async Task<bool> RequestNaturalShutdownAsync(
        Application application,
        MainWindow window)
    {
        try
        {
            if (application.Dispatcher.HasShutdownStarted
                || application.Dispatcher.HasShutdownFinished)
            {
                return false;
            }

            var exitTask = await application.Dispatcher
                .InvokeAsync(window.ExitApplicationAsync)
                .Task
                .WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
            await exitTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (!application.Dispatcher.HasShutdownStarted
                && !application.Dispatcher.HasShutdownFinished)
            {
                await application.Dispatcher
                    .InvokeAsync(() => application.Dispatcher.BeginInvokeShutdown(
                        DispatcherPriority.Normal))
                    .Task
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            }

            return true;
        }
        catch
        {
            if (!application.Dispatcher.HasShutdownStarted
                && !application.Dispatcher.HasShutdownFinished)
            {
                try
                {
                    await application.Dispatcher
                        .InvokeAsync(() =>
                        {
                            application.Shutdown(-1);
                            if (!application.Dispatcher.HasShutdownStarted)
                            {
                                application.Dispatcher.BeginInvokeShutdown(
                                    DispatcherPriority.Normal);
                            }
                        })
                        .Task
                        .WaitAsync(TimeSpan.FromSeconds(5))
                        .ConfigureAwait(false);
                }
                catch
                {
                }
            }

            return false;
        }
    }

    private static async Task WaitForHostAsync(DesktopApiClient api, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                if (await api.PingAsync().ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or DesktopApiException)
            {
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new HarnessFailureException("host_not_ready");
    }

    private static async Task<SessionSnapshotDto> WaitForSessionAsync(
        DesktopApiClient api,
        Func<SessionSnapshotDto, bool> predicate,
        TimeSpan timeout,
        string failureCode)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var snapshot = await api.GetCurrentSessionAsync().ConfigureAwait(false);
            if (snapshot is not null && predicate(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new HarnessFailureException(failureCode);
    }

    private static async Task<AutomationElement> WaitForElementByAutomationIdAsync(
        AutomationElement root,
        string automationId,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                var element = root.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.AutomationIdProperty,
                        automationId));
                if (element is not null)
                {
                    return element;
                }
            }
            catch (ElementNotAvailableException)
            {
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new HarnessFailureException("automation_element_missing");
    }

    private static async Task<AutomationElement> WaitForElementByNameAsync(
        AutomationElement root,
        string name,
        ControlType controlType,
        TimeSpan timeout)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.ControlTypeProperty, controlType));
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                var element = root.FindFirst(TreeScope.Descendants, condition);
                if (element is not null)
                {
                    return element;
                }
            }
            catch (ElementNotAvailableException)
            {
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new HarnessFailureException("automation_text_missing");
    }

    private static async Task WaitForAutomationTextAsync(
        AutomationElement root,
        string automationId,
        string expected,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                var element = root.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.AutomationIdProperty,
                        automationId));
                if (element is not null
                    && string.Equals(element.Current.Name, expected, StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (ElementNotAvailableException)
            {
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new HarnessFailureException("automation_state_missing");
    }

    private static bool ContainsAutomationName(AutomationElement root, string expected)
    {
        try
        {
            if (root.Current.Name.Contains(expected, StringComparison.Ordinal))
            {
                return true;
            }

            return root.FindAll(
                    TreeScope.Descendants,
                    System.Windows.Automation.Condition.TrueCondition)
                .Cast<AutomationElement>()
                .Any(element => element.Current.Name.Contains(expected, StringComparison.Ordinal));
        }
        catch (ElementNotAvailableException)
        {
            throw new HarnessFailureException("automation_tree_unavailable");
        }
    }

    private static void Require(bool condition, string code)
    {
        if (!condition)
        {
            throw new HarnessFailureException(code);
        }
    }

    private static Exception Unwrap(Exception exception) =>
        exception is AggregateException { InnerExceptions.Count: 1 } aggregate
            ? Unwrap(aggregate.InnerExceptions[0])
            : exception;

    private sealed class HarnessFailureException(string code) : Exception
    {
        public string Code { get; } = code;
    }

    private sealed class ControlledVoiceListener : IContinuousVoiceListener
    {
        public bool IsListening { get; private set; }

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<string>? PartialTextChanged;

        public event EventHandler<VoiceUtterance>? UtteranceRecognized;

        public event EventHandler<ContinuousVoiceStatus>? StateChanged;

        public VoiceCapabilityReport GetCapabilityReport() => new(
            RecognitionModelAvailable: true,
            RecognitionProvider: "Controlled voice harness",
            MicrophoneCount: 1,
            ChineseSpeechOutputAvailable: false,
            Message: "Controlled voice harness ready.");

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsListening = true;
            StateChanged?.Invoke(
                this,
                new ContinuousVoiceStatus(ContinuousVoiceState.Listening, "Listening"));
            Started.TrySetResult();
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            IsListening = false;
            StateChanged?.Invoke(
                this,
                new ContinuousVoiceStatus(ContinuousVoiceState.Stopped, "Stopped"));
            return Task.CompletedTask;
        }

        public void Recognize(string text)
        {
            Require(IsListening, "voice_listener_not_started");
            PartialTextChanged?.Invoke(this, text);
            UtteranceRecognized?.Invoke(
                this,
                new VoiceUtterance(text, TimeSpan.FromSeconds(1)));
        }

        public async ValueTask DisposeAsync() =>
            await StopAsync().ConfigureAwait(false);
    }

    private sealed class RecordingChatProvider : IChatModelProvider
    {
        public const string LateReply = "CONTROLLED_LATE_REPLY_MUST_NOT_APPEAR";
        public const string NewReply = "CONTROLLED_REPLACEMENT_REPLY";
        private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();

        private RecordingChatProvider()
        {
        }

        public ChatProviderDescriptor Descriptor { get; } = new(
            "codex",
            "Controlled Codex",
            "Local controlled voice harness",
            SendsDataOffDevice: false,
            [new ChatModelDescriptor(
                "codex-default",
                "Controlled Chat",
                ChatModelCapabilities.Streaming | ChatModelCapabilities.JsonObjectOutput)],
            ChatProviderCredentialKind.None,
            ChatProviderWorkloads.OrdinaryChat | ChatProviderWorkloads.ProgrammingAgent);

        public TaskCompletionSource<Guid> BlockingTurnStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<Guid> CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowLateReply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<Guid> LateReplyReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static RecordingChatProvider Create() => new();

        public Task<ChatProviderHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatProviderHealth(
                Descriptor.ProviderId,
                ChatProviderHealthState.Healthy,
                IsConfigured: true,
                "Controlled provider ready.",
                DateTimeOffset.UtcNow));

        public Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            var completion = CompleteCoreAsync(request, cancellationToken);
            if (request.Prompt?.PromptId != "intent.semantic"
                && request.Messages.Last().Content == OldInput)
            {
                _ = completion.ContinueWith(
                    completed =>
                    {
                        if (completed.Status == TaskStatus.RanToCompletion)
                        {
                            LateReplyReturned.TrySetResult(request.TurnId);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return completion;
        }

        private async Task<ChatModelResponse> CompleteCoreAsync(
            ChatModelRequest request,
            CancellationToken cancellationToken)
        {
            using var local = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                local.Token);
            Require(_active.TryAdd(request.TurnId, local), "provider_turn_already_active");
            try
            {
                var latest = request.Messages.Last().Content;
                if (request.Prompt?.PromptId == "intent.semantic")
                {
                    const string structured =
                        "{\"kind\":\"Conversation\",\"target\":null,\"confidence\":0.99," +
                        "\"isAmbiguous\":false,\"missingContext\":\"None\"}";
                    return Response(request, structured, structured);
                }

                if (latest == OldInput)
                {
                    BlockingTurnStarted.TrySetResult(request.TurnId);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested)
                    {
                        CancellationObserved.TrySetResult(request.TurnId);
                        await AllowLateReply.Task.ConfigureAwait(false);
                        return Response(request, LateReply);
                    }
                }

                Require(latest == NewInput, "provider_received_unexpected_input");
                return Response(request, NewReply);
            }
            finally
            {
                _active.TryRemove(request.TurnId, out _);
            }
        }

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_active.TryGetValue(turnId, out var active))
            {
                active.Cancel();
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            AllowLateReply.TrySetResult();
            foreach (var active in _active.Values)
            {
                active.Cancel();
            }

            _active.Clear();
            return ValueTask.CompletedTask;
        }

        private ChatModelResponse Response(
            ChatModelRequest request,
            string text,
            string? structuredJson = null) =>
            new(
                text,
                ChatFinishReason.Stop,
                new ChatModelUsage(10, 5, 15),
                new ChatProviderMetadata(
                    Descriptor.ProviderId,
                    request.ModelId,
                    $"controlled-{Guid.NewGuid():N}",
                    Descriptor.DataDestination),
                StructuredJson: structuredJson);
    }

    private sealed record HarnessEvidence(
        string OldTurnState,
        string NewTurnState,
        bool FirstInputVoice,
        bool SecondInputVoice,
        bool DistinctSessionTurns,
        bool OldTurnResponding,
        bool UiShowedOldInput,
        bool UiShowedStop,
        bool CancellationObserved,
        bool CancellationTurnMatched,
        bool LateReplyReturned,
        bool NewInputPersisted,
        bool NewReplyPersisted,
        bool LateReplyRejected,
        bool UiShowedNewInput,
        bool UiShowedNewReply,
        bool UiRejectedLateReply);

    private sealed record HarnessResult(
        string Stage,
        string OldTurnState,
        string NewTurnState,
        bool EntryAssemblyConfirmed,
        bool ResourceAssemblyConfirmed,
        bool FirstInputVoice,
        bool SecondInputVoice,
        bool DistinctSessionTurns,
        bool OldTurnResponding,
        bool UiShowedOldInput,
        bool UiShowedStop,
        bool CancellationObserved,
        bool CancellationTurnMatched,
        bool LateReplyReturned,
        bool NewInputPersisted,
        bool NewReplyPersisted,
        bool LateReplyRejected,
        bool UiShowedNewInput,
        bool UiShowedNewReply,
        bool UiRejectedLateReply,
        bool NaturalShutdown,
        long ElapsedMilliseconds,
        string? ErrorCode,
        string? ExceptionType)
    {
        public static HarnessResult Completed(
            HarnessEvidence evidence,
            bool entryAssemblyConfirmed,
            bool resourceAssemblyConfirmed,
            long elapsedMilliseconds) =>
            new(
                "completed",
                evidence.OldTurnState,
                evidence.NewTurnState,
                entryAssemblyConfirmed,
                resourceAssemblyConfirmed,
                evidence.FirstInputVoice,
                evidence.SecondInputVoice,
                evidence.DistinctSessionTurns,
                evidence.OldTurnResponding,
                evidence.UiShowedOldInput,
                evidence.UiShowedStop,
                evidence.CancellationObserved,
                evidence.CancellationTurnMatched,
                evidence.LateReplyReturned,
                evidence.NewInputPersisted,
                evidence.NewReplyPersisted,
                evidence.LateReplyRejected,
                evidence.UiShowedNewInput,
                evidence.UiShowedNewReply,
                evidence.UiRejectedLateReply,
                NaturalShutdown: true,
                elapsedMilliseconds,
                ErrorCode: null,
                ExceptionType: null);

        public static HarnessResult Failed(
            bool entryAssemblyConfirmed,
            bool resourceAssemblyConfirmed,
            bool naturalShutdown,
            long elapsedMilliseconds,
            string errorCode,
            string exceptionType) =>
            new(
                "failed",
                "Unknown",
                "Unknown",
                entryAssemblyConfirmed,
                resourceAssemblyConfirmed,
                FirstInputVoice: false,
                SecondInputVoice: false,
                DistinctSessionTurns: false,
                OldTurnResponding: false,
                UiShowedOldInput: false,
                UiShowedStop: false,
                CancellationObserved: false,
                CancellationTurnMatched: false,
                LateReplyReturned: false,
                NewInputPersisted: false,
                NewReplyPersisted: false,
                LateReplyRejected: false,
                UiShowedNewInput: false,
                UiShowedNewReply: false,
                UiRejectedLateReply: false,
                naturalShutdown,
                elapsedMilliseconds,
                errorCode,
                exceptionType);
    }
}
