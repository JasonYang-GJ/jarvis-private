using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using ScreenGuide.Core.Conversations;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class SessionCoordinatorConflictTests
{
    [Fact]
    public async Task RunningProgrammingTaskAndOrdinaryChatKeepIndependentState()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new ImmediateConversationProvider();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IConversationProvider>(provider));
        await host.StartAsync();
        var (_, project, _) = await environment.SeedProjectsAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("编程与聊天并行");

        var coding = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "帮我修改这个项目并运行测试 TEST_DELAYED_SUCCESS",
            "Text",
            "coding-and-chat-coding",
            session.SessionId));
        await WaitForTurnPhaseAsync(client, coding.TurnId, "WaitingForProject");
        await client.ProvideSessionProjectAsync(session.SessionId, coding.TurnId, project.Id);
        var confirmed = await client.ConfirmSessionTurnAsync(
            session.SessionId,
            coding.TurnId,
            confirmed: true);
        var codingTurn = confirmed.Turns.Single(turn => turn.Id == coding.TurnId);
        var taskId = Assert.IsType<Guid>(codingTurn.TaskId);
        await WaitForTaskStatusAsync(client, taskId, "Running");

        var chat = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "编程任务运行时，我还能问一个普通问题吗？",
            "Text",
            "coding-and-chat-chat",
            session.SessionId));
        var chatCompleted = await WaitForTurnPhaseAsync(client, chat.TurnId, "Completed");
        var whileBothActive = await client.GetTaskAsync(taskId);

        Assert.Equal("Running", whileBothActive?.Summary.Status);
        Assert.Equal(
            "ProgrammingTask",
            chatCompleted.Turns.Single(turn => turn.Id == coding.TurnId).Phase);
        Assert.Equal(
            "Completed",
            chatCompleted.Turns.Single(turn => turn.Id == chat.TurnId).Phase);
        Assert.Equal(
            ["编程任务运行时，我还能问一个普通问题吗？", "普通聊天已回答"],
            chatCompleted.Messages.Select(message => message.Content).ToArray());

        var codingCompleted = await WaitForTurnPhaseAsync(
            client,
            coding.TurnId,
            "Completed",
            TimeSpan.FromSeconds(8));
        var finishedTask = await client.GetTaskAsync(taskId);
        await host.StopAsync();

        Assert.Equal(
            "Completed",
            codingCompleted.Turns.Single(turn => turn.Id == chat.TurnId).Phase);
        Assert.Equal("Succeeded", finishedTask?.Summary.Status);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task RapidSecondMessageCancelsFirstAndCannotMixLateReplyIntoTheSession()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new BargeInConversationProvider();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IConversationProvider>(provider));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("快速插话隔离");

        var first = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "第一条很长的问题",
            "Text",
            "rapid-first",
            session.SessionId));
        await provider.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var secondSubmit = client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "停，改回答第二条",
            "Text",
            "rapid-second",
            session.SessionId));
        await provider.FirstCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var secondWasBlockedUntilCancellationFinished = !secondSubmit.IsCompleted;
        provider.AllowFirstLateReply.TrySetResult();
        var second = await secondSubmit;
        var completed = await WaitForTurnPhaseAsync(client, second.TurnId, "Completed");
        await host.StopAsync();

        Assert.True(secondWasBlockedUntilCancellationFinished);
        Assert.Equal(
            "Cancelled",
            completed.Turns.Single(turn => turn.Id == first.TurnId).Phase);
        Assert.Equal(
            "Completed",
            completed.Turns.Single(turn => turn.Id == second.TurnId).Phase);
        Assert.Equal(
            ["第一条很长的问题", "停，改回答第二条", "第二条的新回答"],
            completed.Messages.Select(message => message.Content).ToArray());
        Assert.DoesNotContain(
            completed.Messages,
            message => message.Content.Contains("第一条迟到回答", StringComparison.Ordinal));
        Assert.Equal(2, provider.SendCount);
        Assert.Equal(1, provider.CancelCount);
    }

    [Fact]
    public async Task SubmittingToANonCurrentSessionCancelsThePreviousCurrentSessionFirst()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new BargeInConversationProvider();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IConversationProvider>(provider));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var olderSession = await client.StartNewSessionAsync("较早话题");
        var currentSession = await client.StartNewSessionAsync("当前话题");
        var currentTurn = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "当前话题中的长回答",
            "Text",
            "switch-session-current-long",
            currentSession.SessionId));
        await provider.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var switchSubmit = client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "回到较早话题继续",
            "Text",
            "switch-session-older-new",
            olderSession.SessionId));
        await provider.FirstCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var switchWaitedForCancellation = !switchSubmit.IsCompleted;
        provider.AllowFirstLateReply.TrySetResult();
        var switchedTurn = await switchSubmit;
        var completed = await WaitForTurnPhaseAsync(client, switchedTurn.TurnId, "Completed");
        var previousSession = await client.SetCurrentSessionAsync(currentSession.SessionId);
        await host.StopAsync();

        Assert.True(switchWaitedForCancellation);
        Assert.Equal(olderSession.SessionId, completed.SessionId);
        Assert.Equal(
            "Cancelled",
            previousSession.Turns.Single(turn => turn.Id == currentTurn.TurnId).Phase);
        Assert.DoesNotContain(
            previousSession.Messages,
            message => message.Content.Contains("第一条迟到回答", StringComparison.Ordinal));
        Assert.Equal(
            "Completed",
            completed.Turns.Single(turn => turn.Id == switchedTurn.TurnId).Phase);
        Assert.Equal(2, provider.SendCount);
        Assert.Equal(1, provider.CancelCount);
    }

    [Fact]
    public async Task CancellingWhileWaitingForWindowConsentNeverCapturesTheWindow()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var foreground = new MutableForegroundProvider(new ForegroundWindowSnapshot(
            9301,
            "等待授权窗口",
            "consent-wait-test",
            93010,
            new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow));
        var capture = new CountingCaptureService();
        var vision = new CountingVisionProvider();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IForegroundWindowContextProvider>(foreground);
            services.AddSingleton<IWindowCaptureService>(capture);
            services.AddSingleton<IWindowVisionProvider>(vision);
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("等待窗口授权时取消");
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "看看这个窗口是什么",
            "Text",
            "cancel-window-consent",
            session.SessionId));
        await WaitForTurnPhaseAsync(client, submitted.TurnId, "WaitingForWindowConsent");

        var cancelled = await client.CancelSessionTurnAsync(session.SessionId, submitted.TurnId);
        await host.StopAsync();

        Assert.Equal(
            "Cancelled",
            cancelled.Turns.Single(turn => turn.Id == submitted.TurnId).Phase);
        Assert.Equal(0, capture.CallCount);
        Assert.Equal(0, vision.CallCount);
    }

    [Fact]
    public async Task CancellingProgrammingSessionTurnCancelsTheRealTaskAndProcessTree()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var (_, project, _) = await environment.SeedProjectsAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("编程任务联动取消");
        var readinessDirectory = Path.Combine(
            environment.RootDirectory,
            $"process-tree-readiness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(readinessDirectory);
        var rootReadinessPath = Path.Combine(readinessDirectory, "root.ready");
        var childReadinessPath = Path.Combine(readinessDirectory, "child.ready");
        var rootReadinessValue = Convert.ToBase64String(Encoding.UTF8.GetBytes(rootReadinessPath));
        var childReadinessValue = Convert.ToBase64String(Encoding.UTF8.GetBytes(childReadinessPath));
        Process? rootProcess = null;
        Process? childProcess = null;

        try
        {
            var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
                $"帮我修改这个项目 TEST_LONG_RUNNING TEST_PROCESS_TREE_BARRIER " +
                $"ROOT_READY_BASE64={rootReadinessValue} " +
                $"CHILD_READY_BASE64={childReadinessValue}",
                "Text",
                "cancel-real-programming-task",
                session.SessionId));
            await WaitForTurnPhaseAsync(client, submitted.TurnId, "WaitingForProject");
            await client.ProvideSessionProjectAsync(session.SessionId, submitted.TurnId, project.Id);
            var confirmed = await client.ConfirmSessionTurnAsync(
                session.SessionId,
                submitted.TurnId,
                confirmed: true);
            var taskId = Assert.IsType<Guid>(
                confirmed.Turns.Single(turn => turn.Id == submitted.TurnId).TaskId);
            var runningDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            rootProcess = await WaitForReadyProcessAsync(
                rootReadinessPath,
                "Root",
                client,
                taskId,
                rootReadinessPath,
                childReadinessPath,
                runningDeadline);
            childProcess = await WaitForReadyProcessAsync(
                childReadinessPath,
                "Child",
                client,
                taskId,
                rootReadinessPath,
                childReadinessPath,
                runningDeadline);
            var runningTask = await WaitForTaskStatusBeforeTerminalAsync(
                client,
                taskId,
                "Running",
                rootProcess.Id,
                childProcess.Id,
                rootReadinessPath,
                childReadinessPath,
                runningDeadline);

            var cancelledSession = await client.CancelSessionTurnAsync(
                session.SessionId,
                submitted.TurnId);
            var rootExit = WaitForProcessExitAsync(
                rootProcess,
                "Root",
                TimeSpan.FromSeconds(8));
            var childExit = WaitForProcessExitAsync(
                childProcess,
                "Child",
                TimeSpan.FromSeconds(8));
            var cancelledTaskStatus = WaitForTaskStatusAsync(
                client,
                taskId,
                "Cancelled",
                TimeSpan.FromSeconds(8));
            await Task.WhenAll(rootExit, childExit, cancelledTaskStatus);
            var cancelledTask = await cancelledTaskStatus;
            await host.StopAsync();

            Assert.Equal("Running", runningTask.Summary.Status);
            Assert.Equal(
                "Cancelled",
                cancelledSession.Turns.Single(turn => turn.Id == submitted.TurnId).Phase);
            Assert.Equal("Cancelled", cancelledTask.Summary.Status);
            Assert.True(rootProcess.HasExited, "取消 Session 后，Fake Codex 根进程仍在运行。");
            Assert.True(childProcess.HasExited, "取消 Session 后，Fake Codex 子进程仍在运行。");
        }
        finally
        {
            rootProcess?.Dispose();
            childProcess?.Dispose();
            await host.StopAsync();
            if (Directory.Exists(readinessDirectory))
            {
                Directory.Delete(readinessDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExplicitSessionStopRejectsAProviderSuccessThatArrivesAfterCancellation()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new BargeInConversationProvider();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IConversationProvider>(provider));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("显式停止迟到回答");
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "这条回答稍后会迟到",
            "Text",
            "explicit-stop-late-reply",
            session.SessionId));
        await provider.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var stop = client.CancelSessionTurnAsync(session.SessionId, submitted.TurnId);
        await provider.FirstCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        provider.AllowFirstLateReply.TrySetResult();
        var cancelled = await stop;
        await host.StopAsync();

        Assert.Equal(
            "Cancelled",
            cancelled.Turns.Single(turn => turn.Id == submitted.TurnId).Phase);
        Assert.Equal(
            ["这条回答稍后会迟到"],
            cancelled.Messages.Select(message => message.Content).ToArray());
        Assert.DoesNotContain(
            cancelled.Messages,
            message => message.Content.Contains("第一条迟到回答", StringComparison.Ordinal));
        Assert.Equal(1, provider.SendCount);
        Assert.Equal(1, provider.CancelCount);
    }

    [Fact]
    public async Task RetryingMissingWindowKeepsTheOriginalRequestAndMovesToConsent()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var foreground = new MutableForegroundProvider(null);
        var capture = new CountingCaptureService();
        var vision = new CountingVisionProvider();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IForegroundWindowContextProvider>(foreground);
            services.AddSingleton<IWindowCaptureService>(capture);
            services.AddSingleton<IWindowVisionProvider>(vision);
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("补齐缺失窗口");
        const string originalRequest = "看看这个窗口是什么";
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            originalRequest,
            "Text",
            "retry-missing-window",
            session.SessionId));
        var waiting = await WaitForTurnPhaseAsync(client, submitted.TurnId, "WaitingForWindow");
        foreground.Current = new ForegroundWindowSnapshot(
            9401,
            "后来切回的窗口",
            "retry-window-test",
            94010,
            new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow);

        var retried = await client.RetrySessionTurnAsync(session.SessionId, submitted.TurnId);
        await host.StopAsync();

        var originalWaitingTurn = waiting.Turns.Single(turn => turn.Id == submitted.TurnId);
        var retriedTurn = retried.Turns.Single(turn => turn.Id == submitted.TurnId);
        Assert.Equal("WaitingForWindow", originalWaitingTurn.Phase);
        Assert.Equal(originalRequest, originalWaitingTurn.InputText);
        Assert.Equal("WaitingForWindowConsent", retriedTurn.Phase);
        Assert.Equal("WindowConsent", retriedTurn.MissingContext);
        Assert.Equal(originalRequest, retriedTurn.InputText);
        Assert.Equal(9401, retriedTurn.WindowHandle);
        Assert.Equal("后来切回的窗口", retriedTurn.WindowTitle);
        Assert.Equal(0, capture.CallCount);
        Assert.Equal(0, vision.CallCount);
    }

    [Fact]
    public async Task SearchConfirmationMustBeRepeatedWhenTheForegroundTargetChanges()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var foreground = new MutableForegroundProvider(new ForegroundWindowSnapshot(
            9501,
            "搜索窗口 A",
            "search-target-a",
            95010,
            new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow));
        var automation = new RecordingDesktopAutomation();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IForegroundWindowContextProvider>(foreground);
            services.AddSingleton<IReliableDesktopAutomation>(automation);
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("搜索目标变化");
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "在当前窗口搜索天气",
            "Text",
            "search-target-changed",
            session.SessionId));
        var firstConfirmation = await WaitForTurnPhaseAsync(
            client,
            submitted.TurnId,
            "WaitingForConfirmation");
        foreground.Current = new ForegroundWindowSnapshot(
            9501,
            "搜索窗口 B",
            "search-target-b",
            95020,
            new DateTimeOffset(2026, 8, 30, 1, 1, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow);

        var mustConfirmAgain = await client.ConfirmSessionTurnAsync(
            session.SessionId,
            submitted.TurnId,
            confirmed: true);
        await host.StopAsync();

        var beforeChange = firstConfirmation.Turns.Single(turn => turn.Id == submitted.TurnId);
        var afterChange = mustConfirmAgain.Turns.Single(turn => turn.Id == submitted.TurnId);
        Assert.Equal(9501, beforeChange.WindowHandle);
        Assert.Equal("搜索窗口 A", beforeChange.WindowTitle);
        Assert.Equal("WaitingForConfirmation", afterChange.Phase);
        Assert.Equal("Confirmation", afterChange.MissingContext);
        Assert.Equal(9501, afterChange.WindowHandle);
        Assert.Equal("搜索窗口 B", afterChange.WindowTitle);
        Assert.True(afterChange.RequiresConfirmation);
        Assert.Contains("窗口已经变化", afterChange.ResultSummary, StringComparison.Ordinal);
        Assert.Equal(0, automation.SearchCallCount);
        Assert.Null(automation.LastQuery);
    }

    [Fact]
    public async Task ConfirmedSearchCarriesHostFrozenIdentityToAutomation()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var expected = new ForegroundWindowSnapshot(
            9551,
            "可信搜索窗口",
            "trusted-search-target",
            95510,
            new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow);
        var foreground = new MutableForegroundProvider(expected);
        var automation = new RecordingDesktopAutomation();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IForegroundWindowContextProvider>(foreground);
            services.AddSingleton<IReliableDesktopAutomation>(automation);
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("可信窗口搜索");
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "在当前窗口搜索天气",
            "Text",
            "trusted-search-identity",
            session.SessionId));
        await WaitForTurnPhaseAsync(client, submitted.TurnId, "WaitingForConfirmation");

        var completed = await client.ConfirmSessionTurnAsync(
            session.SessionId,
            submitted.TurnId,
            confirmed: true);
        await host.StopAsync();

        Assert.Equal(
            "Completed",
            completed.Turns.Single(turn => turn.Id == submitted.TurnId).Phase);
        Assert.Equal(1, automation.SearchCallCount);
        Assert.Equal("天气", automation.LastQuery);
        Assert.NotNull(automation.LastWindowIdentity);
        Assert.True(WindowIdentityContract.Matches(expected, automation.LastWindowIdentity));
    }

    [Fact]
    public async Task SameVisibleWindowWithDifferentProcessIdentityRequiresNewConfirmation()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var started = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var foreground = new MutableForegroundProvider(new ForegroundWindowSnapshot(
            9601,
            "外观完全相同的窗口",
            "same-visible-target",
            96010,
            started,
            DateTimeOffset.UtcNow));
        var automation = new RecordingDesktopAutomation();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IForegroundWindowContextProvider>(foreground);
            services.AddSingleton<IReliableDesktopAutomation>(automation);
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("PID 复用防护");
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "在当前窗口搜索天气",
            "Text",
            "same-visible-different-process",
            session.SessionId));
        await WaitForTurnPhaseAsync(client, submitted.TurnId, "WaitingForConfirmation");
        foreground.Current = new ForegroundWindowSnapshot(
            9601,
            "外观完全相同的窗口",
            "same-visible-target",
            96011,
            started.AddSeconds(1),
            DateTimeOffset.UtcNow);

        var result = await client.ConfirmSessionTurnAsync(
            session.SessionId,
            submitted.TurnId,
            confirmed: true);
        await host.StopAsync();

        var turn = result.Turns.Single(item => item.Id == submitted.TurnId);
        Assert.Equal("WaitingForConfirmation", turn.Phase);
        Assert.Contains("身份不再一致", turn.ResultSummary, StringComparison.Ordinal);
        Assert.Equal(0, automation.SearchCallCount);
    }

    [Fact]
    public async Task HistoricalTurnWithoutProcessIdentityCannotReuseWindowConsent()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var foreground = new MutableForegroundProvider(new ForegroundWindowSnapshot(
            9701,
            "历史窗口",
            "historical-target",
            97010,
            new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow));
        var capture = new CountingCaptureService();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IForegroundWindowContextProvider>(foreground);
            services.AddSingleton<IWindowCaptureService>(capture);
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("历史授权不可复用");
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "看看这个窗口是什么",
            "Text",
            "historical-window-identity",
            session.SessionId));
        await WaitForTurnPhaseAsync(client, submitted.TurnId, "WaitingForWindowConsent");

        await using (var connection = new SqliteConnection(
                         $"Data Source={environment.Options.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE session_turns
                SET window_process_id = NULL, window_process_started_at_utc = NULL
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", submitted.TurnId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        var result = await client.RespondSessionWindowConsentAsync(
            session.SessionId,
            submitted.TurnId,
            granted: true);
        await host.StopAsync();

        var turn = result.Turns.Single(item => item.Id == submitted.TurnId);
        Assert.Equal("WaitingForWindowConsent", turn.Phase);
        Assert.Contains("身份不再一致", turn.ResultSummary, StringComparison.Ordinal);
        Assert.Equal(0, capture.CallCount);
    }

    private static async Task<SessionSnapshotDto> WaitForTurnPhaseAsync(
        IDesktopApiClient client,
        Guid turnId,
        string expectedPhase,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await client.GetCurrentSessionAsync();
            if (snapshot?.Turns.SingleOrDefault(turn => turn.Id == turnId)?.Phase == expectedPhase)
            {
                return snapshot;
            }

            await Task.Delay(20);
        }

        var last = await client.GetCurrentSessionAsync();
        var lastTurn = last?.Turns.SingleOrDefault(turn => turn.Id == turnId);
        if (lastTurn?.Phase == expectedPhase) return last!;
        var task = lastTurn?.TaskId is { } taskId ? await client.GetTaskAsync(taskId) : null;
        throw new TimeoutException($"统一会话请求没有进入 {expectedPhase}；Turn={lastTurn?.Phase}; Task={task?.Summary.Status}; cancelled={lastTurn?.CancellationRequested}。");
    }

    private static async Task<TaskDetailsDto> WaitForTaskStatusAsync(
        IDesktopApiClient client,
        Guid taskId,
        string expectedStatus,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var details = await client.GetTaskAsync(taskId);
            if (details?.Summary.Status == expectedStatus)
            {
                return details;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"编程任务没有进入 {expectedStatus}。");
    }

    private static async Task<Process> WaitForReadyProcessAsync(
        string readinessPath,
        string processRole,
        IDesktopApiClient client,
        Guid taskId,
        string rootReadinessPath,
        string childReadinessPath,
        DateTimeOffset deadline)
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(readinessPath))
            {
                var value = await File.ReadAllTextAsync(readinessPath);
                if (!int.TryParse(value, out var processId) || processId <= 0)
                {
                    throw new InvalidOperationException(
                        $"{processRole} readiness PID 无效；RootReady={File.Exists(rootReadinessPath)}；" +
                        $"ChildReady={File.Exists(childReadinessPath)}。");
                }

                var process = Process.GetProcessById(processId);
                _ = process.Handle;
                if (process.HasExited
                    || !string.Equals(
                        process.ProcessName,
                        "ScreenGuide.FakeCodexCli",
                        StringComparison.OrdinalIgnoreCase))
                {
                    process.Dispose();
                    throw new InvalidOperationException(
                        $"{processRole} readiness 没有指向活动 Fake Codex 进程；PID={processId}；" +
                        $"RootReady={File.Exists(rootReadinessPath)}；" +
                        $"ChildReady={File.Exists(childReadinessPath)}。");
                }

                return process;
            }

            var details = await client.GetTaskAsync(taskId);
            if (details is not null && IsTerminalTaskStatus(details.Summary.Status))
            {
                throw new InvalidOperationException(
                    $"编程任务在 readiness 建立前已经终止；Status={details.Summary.Status}；" +
                    "ErrorCode=not_exposed；" +
                    $"RootReady={File.Exists(rootReadinessPath)}；" +
                    $"ChildReady={File.Exists(childReadinessPath)}。");
            }

            await Task.Delay(20);
        }

        var lastDetails = await client.GetTaskAsync(taskId);
        throw new TimeoutException(
            $"等待 {processRole} readiness 超时；Status={lastDetails?.Summary.Status ?? "Missing"}；" +
            "ErrorCode=not_exposed；" +
            $"RootReady={File.Exists(rootReadinessPath)}；" +
            $"ChildReady={File.Exists(childReadinessPath)}。");
    }

    private static async Task<TaskDetailsDto> WaitForTaskStatusBeforeTerminalAsync(
        IDesktopApiClient client,
        Guid taskId,
        string expectedStatus,
        int rootProcessId,
        int childProcessId,
        string rootReadinessPath,
        string childReadinessPath,
        DateTimeOffset deadline)
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            var details = await client.GetTaskAsync(taskId);
            if (details?.Summary.Status == expectedStatus)
            {
                return details;
            }

            if (details is not null && IsTerminalTaskStatus(details.Summary.Status))
            {
                throw new InvalidOperationException(
                    $"编程任务在进入 {expectedStatus} 前已经终止；Status={details.Summary.Status}；" +
                    "ErrorCode=not_exposed；" +
                    $"RootPid={rootProcessId}；ChildPid={childProcessId}；" +
                    $"RootReady={File.Exists(rootReadinessPath)}；" +
                    $"ChildReady={File.Exists(childReadinessPath)}。");
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(
            $"编程任务没有进入 {expectedStatus}；RootPid={rootProcessId}；ChildPid={childProcessId}；" +
            $"RootReady={File.Exists(rootReadinessPath)}；" +
            $"ChildReady={File.Exists(childReadinessPath)}。");
    }

    private static async Task WaitForProcessExitAsync(
        Process process,
        string processRole,
        TimeSpan timeout)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"取消 Session 后等待 {processRole} 进程退出超时；PID={process.Id}。",
                exception);
        }
    }

    private static bool IsTerminalTaskStatus(string status) =>
        status is "Completed" or "Failed" or "Cancelled";

    private sealed class ImmediateConversationProvider : IConversationProvider
    {
        public string ProviderId => "session-conflict-immediate";

        public List<ConversationProviderRequest> Requests { get; } = [];

        public async Task<ConversationProviderResult> SendAsync(
            ConversationProviderRequest request,
            Func<string, int, Task>? started = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (started is not null)
            {
                await started("thread-session-conflict", 9101);
            }

            return new ConversationProviderResult(
                ConversationProviderOutcome.Succeeded,
                "thread-session-conflict",
                "普通聊天已回答",
                "message-session-conflict",
                9101);
        }

        public Task CancelAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BargeInConversationProvider : IConversationProvider
    {
        private int _sendCount;
        private int _cancelCount;

        public string ProviderId => "session-conflict-barge-in";

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowFirstLateReply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SendCount => Volatile.Read(ref _sendCount);

        public int CancelCount => Volatile.Read(ref _cancelCount);

        public async Task<ConversationProviderResult> SendAsync(
            ConversationProviderRequest request,
            Func<string, int, Task>? started = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _sendCount);
            if (started is not null)
            {
                await started("thread-session-barge-in", 9200 + call);
            }

            if (call == 1)
            {
                using var registration = cancellationToken.Register(
                    () => FirstCancellationObserved.TrySetResult());
                FirstStarted.TrySetResult();
                await AllowFirstLateReply.Task;
                return new ConversationProviderResult(
                    ConversationProviderOutcome.Succeeded,
                    "thread-session-barge-in",
                    "第一条迟到回答",
                    "message-session-barge-in-old",
                    9201);
            }

            return new ConversationProviderResult(
                ConversationProviderOutcome.Succeeded,
                "thread-session-barge-in",
                "第二条的新回答",
                "message-session-barge-in-new",
                9202);
        }

        public Task CancelAsync(Guid conversationId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _cancelCount);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            AllowFirstLateReply.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableForegroundProvider(ForegroundWindowSnapshot? current)
        : IForegroundWindowContextProvider
    {
        public ForegroundWindowSnapshot? Current { get; set; } = current;

        public ForegroundWindowSnapshot? GetLastExternalWindow() => Current;
    }

    private sealed class CountingCaptureService : IWindowCaptureService
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<CapturedWindowFrame> CaptureAsync(
            WindowCaptureTarget target,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new CapturedWindowFrame([4, 5, 6], 10, 10, "synthetic"));
        }
    }

    private sealed class CountingVisionProvider : IWindowVisionProvider
    {
        private int _callCount;

        public string ProviderId => "session-conflict-window";

        public bool SendsImageOffDevice => false;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<WindowVisionResult> AnalyzeAsync(
            WindowVisionRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new WindowVisionResult(
                "合成窗口",
                "Synthetic",
                ["合成事实"],
                ["无真实数据"]));
        }
    }

    private sealed class RecordingDesktopAutomation : IReliableDesktopAutomation
    {
        private int _searchCallCount;

        public int SearchCallCount => Volatile.Read(ref _searchCallCount);

        public string? LastQuery { get; private set; }

        public ForegroundWindowSnapshot? LastWindowIdentity { get; private set; }

        public DesktopAutomationResult Search(ForegroundWindowSnapshot expectedWindow, string query)
        {
            Interlocked.Increment(ref _searchCallCount);
            LastQuery = query;
            LastWindowIdentity = expectedWindow;
            return new DesktopAutomationResult(true, "合成搜索已提交");
        }

        public DesktopAutomationResult Describe(ForegroundWindowSnapshot expectedWindow) =>
            new(true, "合成结构化窗口信息");
    }
}
