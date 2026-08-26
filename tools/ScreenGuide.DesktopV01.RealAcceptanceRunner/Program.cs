using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScreenGuide.Agent.Codex;
using ScreenGuide.AI.Core;
using ScreenGuide.AI.DeepSeek;
using ScreenGuide.Core.Ai;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.DesktopV01.RealAcceptanceRunner;
using ScreenGuide.Skills.Windows;

const string R3ResultPrefix = "YUANSHU_R3_DEEPSEEK_RESULT ";
var r3Validation = R3DeepSeekValidationOptions.Parse(args);
if (r3Validation.Requested && !r3Validation.IsValid)
{
    Console.WriteLine(R3ResultPrefix + JsonSerializer.Serialize(new
    {
        Stage = "guard",
        Passed = false,
        ErrorCode = r3Validation.ErrorCode,
        RealProvider = false
    }));
    return 2;
}

var stage2R3DeepSeek = r3Validation.IsValid;
var smokeOnly = args.Any(argument =>
    string.Equals(argument, "--smoke", StringComparison.OrdinalIgnoreCase));
var desktopActionSmoke = args.Any(argument =>
    string.Equals(argument, "--desktop-action-smoke", StringComparison.OrdinalIgnoreCase));
var conversationSmoke = args.Any(argument =>
    string.Equals(argument, "--conversation-smoke", StringComparison.OrdinalIgnoreCase));
var stage1Session = args.Any(argument =>
    string.Equals(argument, "--stage1-session", StringComparison.OrdinalIgnoreCase));
var stage2LiveProviders = args.Any(argument =>
    string.Equals(argument, "--stage2-live-providers", StringComparison.OrdinalIgnoreCase));
var trackHost = args.Any(argument =>
    string.Equals(argument, "--track-host", StringComparison.OrdinalIgnoreCase));
var repositoryRoot = FindRepositoryRoot();
var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var runRoot = Path.Combine(
    localData,
    "ScreenGuide",
    "Experiments",
    "DesktopV01",
    DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
var dataRoot = Path.Combine(runRoot, "user-data");
var projectRoot = Path.Combine(runRoot, "authorized-project");
Directory.CreateDirectory(dataRoot);
Directory.CreateDirectory(projectRoot);
await PrepareProjectAsync(projectRoot);
await File.WriteAllTextAsync(
    Path.Combine(dataRoot, "client-settings.json"),
    JsonSerializer.Serialize(
        new
        {
            startWithWindows = false,
            runInBackground = true,
            closeToTray = true,
            notificationsEnabled = true,
            onboardingCompleted = true,
            notificationStateInitialized = true,
            deliveredNotificationKeys = Array.Empty<string>()
        },
        new JsonSerializerOptions { WriteIndented = true }));

var publishRoot = Path.Combine(repositoryRoot, "artifacts", "publish", "win-x64");
var clientPath = GetOption(args, "--client-path=")
                 ?? Environment.GetEnvironmentVariable("SCREEN_GUIDE_ACCEPTANCE_CLIENT_PATH")
                 ?? Path.Combine(publishRoot, "ScreenGuide.DesktopClient.exe");
var hostPath = GetOption(args, "--host-path=")
               ?? Environment.GetEnvironmentVariable("SCREEN_GUIDE_ACCEPTANCE_HOST_PATH")
               ?? Path.Combine(publishRoot, "ScreenGuide.DesktopHost.exe");
if (!File.Exists(clientPath) || !File.Exists(hostPath))
{
    throw new FileNotFoundException("请先运行 scripts/build-desktop-release.ps1。 ");
}

var pipeName = $"ScreenGuide.DesktopV01.Real.{Guid.NewGuid():N}";
Process? trackedHost = null;
IHost? r3Host = null;
R3BudgetedChatModelProvider? r3Provider = null;
if (stage2R3DeepSeek)
{
    var r3Budget = r3Validation.Budget!;
    r3Host = DesktopHostFactory.Build(
        [],
        new DesktopHostOptions(dataRoot, pipeName: pipeName),
        services =>
        {
            services.AddSingleton(serviceProvider =>
                new R3BudgetedChatModelProvider(
                    new DeepSeekChatModelProvider(
                        serviceProvider.GetRequiredService<IProviderCredentialStore>()),
                    r3Budget));
            services.AddSingleton(serviceProvider => new ChatProviderRegistry(
            [
                serviceProvider.GetRequiredService<CodexChatModelProvider>(),
                serviceProvider.GetRequiredService<R3BudgetedChatModelProvider>()
            ]));
        });
    using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await r3Host.StartAsync(startupTimeout.Token);
    r3Provider = r3Host.Services.GetRequiredService<R3BudgetedChatModelProvider>();
}

if (trackHost && !stage2R3DeepSeek)
{
    var hostStartInfo = new ProcessStartInfo
    {
        FileName = hostPath,
        WorkingDirectory = Path.GetDirectoryName(hostPath)!,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    hostStartInfo.Environment["SCREEN_GUIDE_DATA_DIRECTORY"] = dataRoot;
    hostStartInfo.Environment["SCREEN_GUIDE_PIPE_NAME"] = pipeName;
    trackedHost = Process.Start(hostStartInfo)
        ?? throw new InvalidOperationException("用于诊断的 Desktop Host 未启动。 ");
}

var startInfo = new ProcessStartInfo
{
    FileName = clientPath,
    WorkingDirectory = Path.GetDirectoryName(clientPath)!,
    UseShellExecute = false
};
startInfo.ArgumentList.Add("--show");
startInfo.Environment["SCREEN_GUIDE_DATA_DIRECTORY"] = dataRoot;
startInfo.Environment["SCREEN_GUIDE_PIPE_NAME"] = pipeName;
startInfo.Environment["SCREEN_GUIDE_DESKTOP_HOST_PATH"] = hostPath;
using var client = Process.Start(startInfo)
    ?? throw new InvalidOperationException("最终 Release DesktopClient 未启动。 ");
var api = new DesktopApiClient(pipeName, TimeSpan.FromSeconds(10));
var results = new List<object>();
try
{
    await WaitAsync(() => api.PingAsync(), TimeSpan.FromSeconds(20));
    await WaitAsync(
        () =>
        {
            client.Refresh();
            return Task.FromResult(!client.HasExited && client.MainWindowHandle != IntPtr.Zero);
        },
        TimeSpan.FromSeconds(15));
    var status = await api.GetSystemStatusAsync();
    if (!stage2R3DeepSeek
        && (!status.Codex.IsCompatible || status.Codex.Version != "0.147.0"))
    {
        throw new InvalidOperationException(
            $"Codex 兼容门禁未通过：{status.Codex.Version ?? "not-found"}。 ");
    }

    if (stage2R3DeepSeek)
    {
        try
        {
            var result = await RunStage2R3DeepSeekPreparationAsync(
                api,
                r3Host!,
                r3Provider!,
                r3Validation.Budget!);
            Console.WriteLine(R3ResultPrefix + JsonSerializer.Serialize(result));
            return result.Passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.WriteLine(R3ResultPrefix + JsonSerializer.Serialize(new
            {
                Stage = "failed",
                Passed = false,
                ErrorCode = SafeR3ErrorCode(exception),
                ExceptionType = exception.GetType().Name,
                RealProvider = true
            }));
            return 1;
        }
    }
    else if (stage2LiveProviders)
    {
        await RunStage2LiveProviderAcceptanceAsync(api, projectRoot, results);
    }
    else if (stage1Session)
    {
        await RunStage1SessionAcceptanceAsync(api, projectRoot, results);
    }
    else if (conversationSmoke)
    {
        var validationWord = $"蓝鹭-{Guid.NewGuid():N}";
        var conversation = await api.CreateConversationAsync("真实连续对话验收");
        var first = await api.SendConversationMessageAsync(
            conversation.Id,
            $"请记住校验词“{validationWord}”，现在只回答“已记住”。",
            $"real-chat-first-{Guid.NewGuid():N}");
        var firstDetails = await WaitForConversationAsync(
            api,
            conversation.Id,
            2,
            TimeSpan.FromMinutes(2));
        var second = await api.SendConversationMessageAsync(
            conversation.Id,
            "我刚才让你记住的校验词是什么？只回答校验词。",
            $"real-chat-second-{Guid.NewGuid():N}");
        var secondDetails = await WaitForConversationAsync(
            api,
            conversation.Id,
            4,
            TimeSpan.FromMinutes(2));
        var passed = !first.WasDuplicate
                     && !second.WasDuplicate
                     && firstDetails.Summary.Status == "Ready"
                     && secondDetails.Summary.Status == "Ready"
                     && secondDetails.Messages[^1].Content.Contains(validationWord, StringComparison.Ordinal)
                     && secondDetails.Turns.Count == 2
                     && secondDetails.Turns.All(turn => turn.Status == "Succeeded");
        results.Add(new
        {
            Number = 1,
            Scenario = "real two-turn local conversation",
            Status = secondDetails.Summary.Status,
            Verification = "ConversationContinuity",
            ThreadId = (string?)null,
            CurrentAttempt = 2,
            Passed = passed,
            UserSummary = secondDetails.Messages[^1].Content
        });
        Console.WriteLine($"[conversation] status={secondDetails.Summary.Status}, continuity={passed}");
        if (!passed)
        {
            Console.WriteLine(JsonSerializer.Serialize(secondDetails, DesktopProtocolJson.Options));
            throw new InvalidOperationException("真实连续对话验收未通过。 ");
        }
    }
    else if (desktopActionSmoke)
    {
        var applications = await api.ListDesktopApplicationsAsync();
        if (!applications.Any(item => item.Id == "notepad"))
        {
            throw new InvalidOperationException("真实验收没有取得受控应用清单。 ");
        }

        var action = await api.ExecuteDesktopActionAsync(new ExecuteDesktopActionRequestDto(
            "OpenApplication",
            "notepad",
            true,
            $"real-desktop-action-{Guid.NewGuid():N}"));
        var passed = action.Succeeded && !action.WasDuplicate && action.InvocationId is not null;
        results.Add(new
        {
            Number = 1,
            Scenario = "explicitly confirmed allowlisted desktop action",
            Status = action.Succeeded ? "Succeeded" : "Failed",
            Verification = "WindowsAccepted",
            ThreadId = (string?)null,
            CurrentAttempt = 0,
            Passed = passed,
            UserSummary = action.Message
        });
        Console.WriteLine($"[desktop-action] status={action.Succeeded}, pass={passed}");
        if (action.ProcessId is { } processId)
        {
            TryCloseAcceptanceProcess(processId);
        }

        if (!passed)
        {
            throw new InvalidOperationException("真实桌面安全启动验收未通过。 ");
        }
    }

    var project = desktopActionSmoke || conversationSmoke || stage1Session || stage2LiveProviders
        ? null
        : await api.AddProjectAsync(new AddProjectRequestDto(
            projectRoot,
            "Desktop V0.1 real acceptance"));
    var regularTaskCount = desktopActionSmoke || conversationSmoke || stage1Session || stage2LiveProviders
        ? 0
        : smokeOnly
            ? 1
            : 18;
    for (var number = 1; number <= regularTaskCount; number++)
    {
        var fileName = $"result-{number:D2}.txt";
        var result = await api.CreateTaskAsync(new CreateTaskRequestDto(
            project!.Id,
            $"""
            这是 ScreenGuide Desktop 真实桌面验收任务 {number}/{(smokeOnly ? 1 : 20)}。
            在当前已授权项目中新建 {fileName}，内容只写 desktop-acceptance-{number:D2}。
            不要修改其他文件。必须实际运行：dotnet test Acceptance.csproj --nologo --no-restore。
            完成后按系统提供的 Schema 如实返回修改文件和测试结果。
            """,
            $"真实桌面验收 {number:D2}"));
        var details = await WaitForTerminalEvidenceAsync(api, result.TaskId, TimeSpan.FromMinutes(2));
        var passed = details.Summary.Status == "Succeeded"
                     && details.Evidence?.VerificationStatus == "Verified"
                     && details.Evidence.ChangedFiles.Any(file => file.RelativePath == fileName)
                     && details.Evidence.TestStatus == "Passed"
                     && !string.IsNullOrWhiteSpace(details.ThreadId);
        results.Add(ToResult(number, "verified desktop task", details, passed));
        Console.WriteLine(
            $"[{number:D2}/{(smokeOnly ? 1 : 20)}] status={details.Summary.Status}, verification={details.Evidence?.VerificationStatus}, pass={passed}");
        if (!passed)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                details,
                DesktopProtocolJson.Options));
            throw new InvalidOperationException($"真实桌面任务 {number} 未通过证据门禁。 ");
        }
    }

    if (!smokeOnly
        && !desktopActionSmoke
        && !conversationSmoke
        && !stage1Session
        && !stage2LiveProviders)
    {
    var waitingCommand = await api.CreateTaskAsync(new CreateTaskRequestDto(
        project!.Id,
        """
        这是 ScreenGuide Desktop V0.1 真实桌面验收任务 19/20。
        现在不要修改文件、不要运行命令。按 Schema 返回 action_required，问题为“是否创建 result-19.txt 并运行测试？”，选项为 GO。
        """,
        "等待用户后继续"));
    var waiting = await WaitForStatusAsync(api, waitingCommand.TaskId, "WaitingForUser", TimeSpan.FromMinutes(2));
    var originalThread = waiting.ThreadId;
    await WaitAsync(
        () => Task.FromResult(HasDeliveredNotification(
            dataRoot,
            waitingCommand.TaskId,
            "WaitingForUser")),
        TimeSpan.FromSeconds(15));
    await api.ContinueTaskAsync(
        waitingCommand.TaskId,
        "GO。新建 result-19.txt，内容只写 desktop-acceptance-19；必须实际运行 dotnet test Acceptance.csproj --nologo --no-restore，并如实返回结果。 ");
    var resumed = await WaitForTerminalEvidenceAsync(api, waitingCommand.TaskId, TimeSpan.FromMinutes(2));
    var resumedPassed = resumed.Summary.Status == "Succeeded"
                        && resumed.Evidence?.VerificationStatus == "Verified"
                        && resumed.ThreadId == originalThread
                        && resumed.CurrentAttempt == 2;
    results.Add(ToResult(19, "action_required same-thread resume", resumed, resumedPassed));
    Console.WriteLine(
        $"[19/20] status={resumed.Summary.Status}, sameThread={resumed.ThreadId == originalThread}, pass={resumedPassed}");
    if (!resumedPassed)
    {
        throw new InvalidOperationException("等待用户后的同 Thread 续接未通过。 ");
    }

    var cancelMarker = Path.Combine(projectRoot, "must-not-exist-after-cancel.txt");
    var cancelCommand = await api.CreateTaskAsync(new CreateTaskRequestDto(
        project!.Id,
        $"""
        这是 ScreenGuide Desktop V0.1 真实桌面验收任务 20/20。
        请立即运行并等待：powershell -NoProfile -Command "Start-Sleep -Seconds 60; Set-Content -LiteralPath '{cancelMarker}' -Value 'cancel failed'"
        命令结束后再按 Schema 返回 completed。
        """,
        "真实取消与子进程停止"));
    _ = await WaitForStatusAsync(api, cancelCommand.TaskId, "Running", TimeSpan.FromSeconds(20));
    await WaitAsync(
        async () =>
        {
            var details = await api.GetTaskAsync(cancelCommand.TaskId);
            return details?.Events.Any(item =>
                item.Message.Contains("in_progress", StringComparison.Ordinal)) == true;
        },
        TimeSpan.FromSeconds(30));
    await api.CancelTaskAsync(cancelCommand.TaskId, $"desktop-v01-cancel-{Guid.NewGuid():N}");
    var cancelled = await WaitForStatusAsync(api, cancelCommand.TaskId, "Cancelled", TimeSpan.FromSeconds(30));
    await Task.Delay(TimeSpan.FromSeconds(3));
    var cancelPassed = cancelled.Summary.Status == "Cancelled" && !File.Exists(cancelMarker);
    results.Add(ToResult(20, "real cancellation process tree", cancelled, cancelPassed));
    Console.WriteLine($"[20/20] status={cancelled.Summary.Status}, markerExists={File.Exists(cancelMarker)}, pass={cancelPassed}");
    if (!cancelPassed)
    {
        throw new InvalidOperationException("真实取消未能确认子进程停止。 ");
    }

    await WaitAsync(
        () => Task.FromResult(DeliveredNotificationCount(dataRoot) >= 20),
        TimeSpan.FromSeconds(15));
    }
    Console.WriteLine(JsonSerializer.Serialize(
        new
        {
            RuntimeRoot = runRoot,
            Total = results.Count,
            Passed = results.Count(result => (bool)result.GetType().GetProperty("Passed")!.GetValue(result)!),
            Failed = results.Count(result => !(bool)result.GetType().GetProperty("Passed")!.GetValue(result)!),
            FalseCompleted = results.Count(result =>
                (string)result.GetType().GetProperty("Status")!.GetValue(result)! == "Succeeded"
                && !(bool)result.GetType().GetProperty("Passed")!.GetValue(result)!),
            NotificationEventsDelivered = DeliveredNotificationCount(dataRoot),
            Results = results
        },
        new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    if (stage2LiveProviders || stage2R3DeepSeek)
    {
        try
        {
            await api.SetChatRouteAsync(new SetChatRouteRequestDto("codex", "codex-default"));
            _ = await api.DeleteProviderCredentialAsync(new ProviderIdRequestDto("deepseek"));
            if (!stage2R3DeepSeek)
            {
                Console.WriteLine("[stage2 cleanup] isolated DeepSeek credential deleted, chat route restored to Codex");
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[stage2 cleanup] failed safely: {exception.GetType().Name}");
        }
    }

    if (!client.HasExited)
    {
        client.Kill(entireProcessTree: false);
        await client.WaitForExitAsync();
    }

    if (await api.PingAsync())
    {
        await api.ShutdownHostAsync();
    }

    if (r3Host is not null)
    {
        using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await r3Host.StopAsync(shutdownTimeout.Token);
        r3Host.Dispose();
    }

    if (trackedHost is not null)
    {
        try
        {
            await trackedHost.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            trackedHost.Kill(entireProcessTree: true);
            await trackedHost.WaitForExitAsync();
        }

        Console.WriteLine($"[tracked-host] exitCode={trackedHost.ExitCode}");
        trackedHost.Dispose();
    }
}

return 0;

static async Task<R3DeepSeekValidationResult> RunStage2R3DeepSeekPreparationAsync(
    IDesktopApiClient api,
    IHost host,
    R3BudgetedChatModelProvider provider,
    R3DeepSeekValidationBudget budget)
{
    using var totalTimeout = new CancellationTokenSource(budget.TotalTimeout);
    var cancellationToken = totalTimeout.Token;
    var stopwatch = Stopwatch.StartNew();
    Console.WriteLine(
        "[r3] 请只在可见的元枢设置页保存 DeepSeek Key，并选择要验收的 V4 模型；不要把 Key 输入命令行，也不要手动点击健康检查。");
    Console.Out.Flush();

    var settings = await WaitForDeepSeekConfigurationOnlyAsync(api, cancellationToken);
    var modelId = settings.CurrentChatRoute.ModelId;
    var health = await api.CheckAiProviderHealthAsync(new ProviderIdRequestDto("deepseek"))
        .WaitAsync(cancellationToken);
    var healthPassed = health.State == "Healthy";
    RequireR3(healthPassed, "r3_health_failed");

    var invocations = host.Services.GetRequiredService<IAiInvocationStore>();
    var session = await api.StartNewSessionAsync("R3 DeepSeek 隔离验收")
        .WaitAsync(cancellationToken);

    var ordinary = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
            "R3 ordinary chat canary. Reply only with R3-CHAT-OK.",
            "Text",
            $"r3-chat-{Guid.NewGuid():N}",
            session.SessionId))
        .WaitAsync(cancellationToken);
    var ordinarySnapshot = await WaitForSessionTurnAsync(
            api,
            ordinary.TurnId,
            ["Completed"],
            budget.TotalTimeout)
        .WaitAsync(cancellationToken);
    var ordinaryTurn = ordinarySnapshot.Turns.Single(item => item.Id == ordinary.TurnId);
    var ordinaryInvocation = AssertR3ConversationInvocation(
        await invocations.GetForSessionTurnAsync(ordinary.TurnId, cancellationToken),
        provider,
        ordinaryTurn,
        modelId,
        AiInvocationStatus.Succeeded,
        requireProviderMetadata: true);
    var ordinaryChatPassed = ordinaryTurn.Phase == "Completed"
                             && ordinaryInvocation.Status == AiInvocationStatus.Succeeded;
    RequireR3(ordinaryChatPassed, "r3_ordinary_chat_failed");

    var streamingObservation = provider.PrepareNextCall(R3ValidationCallMode.Streaming);
    var streaming = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
            "R3 streaming canary. Write the numbers 1 through 20, one per line.",
            "Text",
            $"r3-stream-{Guid.NewGuid():N}",
            session.SessionId))
        .WaitAsync(cancellationToken);
    var streamingSnapshot = await WaitForSessionTurnAsync(
            api,
            streaming.TurnId,
            ["Completed"],
            budget.TotalTimeout)
        .WaitAsync(cancellationToken);
    await streamingObservation.Terminal.WaitAsync(cancellationToken);
    var streamingTurn = streamingSnapshot.Turns.Single(item => item.Id == streaming.TurnId);
    _ = AssertR3ConversationInvocation(
        await invocations.GetForSessionTurnAsync(streaming.TurnId, cancellationToken),
        provider,
        streamingTurn,
        modelId,
        AiInvocationStatus.Succeeded,
        requireProviderMetadata: true);
    var streamingPassed = streamingObservation.DeltaCount >= 2
                          && streamingObservation.FinalUpdateObserved
                          && streamingObservation.ResponseReturned;
    RequireR3(streamingPassed, "r3_streaming_failed");

    const string cancellationCanary = "R3-CANCEL-LATE-CANARY";
    var cancellationObservation = provider.PrepareNextCall(
        R3ValidationCallMode.StreamingCancellation);
    var cancelling = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
            $"{cancellationCanary}: write 100 numbered short lines and do not summarize.",
            "Text",
            $"r3-cancel-{Guid.NewGuid():N}",
            session.SessionId))
        .WaitAsync(cancellationToken);
    await cancellationObservation.AtLeastTwoDeltas.WaitAsync(cancellationToken);
    var cancelledSnapshot = await api.CancelSessionTurnAsync(session.SessionId, cancelling.TurnId)
        .WaitAsync(cancellationToken);
    await cancellationObservation.Terminal.WaitAsync(cancellationToken);
    var cancelledTurn = cancelledSnapshot.Turns.Single(item => item.Id == cancelling.TurnId);
    _ = AssertR3ConversationInvocation(
        await invocations.GetForSessionTurnAsync(cancelling.TurnId, cancellationToken),
        provider,
        cancelledTurn,
        modelId,
        AiInvocationStatus.Cancelled,
        requireProviderMetadata: false);
    var lateDeltaRejected = cancellationObservation.DeltaCountWhenCancellationCompleted
                            == cancellationObservation.DeltaCount;
    var lateFinalRejected = !cancellationObservation.FinalUpdateObserved;
    var lateSuccessRejected = !cancellationObservation.ResponseReturned
                              && cancelledTurn.Phase == "Cancelled"
                              && !cancelledSnapshot.Messages.Any(message =>
                                  message.Role == "Assistant"
                                  && message.Content.Contains(
                                      cancellationCanary,
                                      StringComparison.Ordinal));
    var cancellationPassed = lateDeltaRejected && lateFinalRejected && lateSuccessRejected;
    RequireR3(cancellationPassed, "r3_cancellation_failed");

    var auditPassed = ordinaryInvocation.ProviderId == DeepSeekChatModelProvider.ProviderId
                      && ordinaryInvocation.ModelId == modelId
                      && ordinaryInvocation.DataDestination == DeepSeekChatModelProvider.DataDestination
                      && !string.IsNullOrWhiteSpace(ordinaryInvocation.PromptId)
                      && !string.IsNullOrWhiteSpace(ordinaryInvocation.PromptVersion)
                      && !string.IsNullOrWhiteSpace(ordinaryInvocation.PromptContentHash)
                      && ordinaryInvocation.SessionTurnId == ordinary.TurnId
                      && ordinaryInvocation.ConversationTurnId == ordinaryTurn.ConversationTurnId
                      && ordinaryInvocation.Usage is not null
                      && !string.IsNullOrWhiteSpace(ordinaryInvocation.ProviderRequestId)
                      && ordinaryInvocation.CompletedAtUtc is not null;
    RequireR3(auditPassed, "r3_audit_failed");
    RequireR3(provider.RequestCount == 4, "r3_request_count_mismatch");

    stopwatch.Stop();
    return new R3DeepSeekValidationResult(
        Stage: "complete",
        Passed: true,
        DeepSeekChatModelProvider.ProviderId,
        modelId,
        provider.RequestCount,
        healthPassed,
        ordinaryChatPassed,
        streamingPassed,
        streamingObservation.DeltaCount,
        cancellationPassed,
        cancellationObservation.DeltaCount,
        lateDeltaRejected,
        lateFinalRejected,
        lateSuccessRejected,
        auditPassed,
        budget.NoAutomaticRetry,
        budget.NoFallback,
        stopwatch.ElapsedMilliseconds,
        ErrorCode: null);
}

static AiInvocationRecord AssertR3ConversationInvocation(
    IReadOnlyList<AiInvocationRecord> records,
    R3BudgetedChatModelProvider provider,
    UnifiedSessionTurnDto turn,
    string modelId,
    AiInvocationStatus expectedStatus,
    bool requireProviderMetadata)
{
    var invocation = records.Single(record => record.Purpose == AiInvocationPurpose.Conversation);
    var identity = provider.RequestIdentities.Single(item => item.TurnId == turn.Id);
    var matches = invocation.Id == identity.RequestId
                  && invocation.SessionTurnId == turn.Id
                  && invocation.ConversationTurnId == turn.ConversationTurnId
                  && invocation.ProviderId == DeepSeekChatModelProvider.ProviderId
                  && invocation.ModelId == modelId
                  && invocation.DataDestination == DeepSeekChatModelProvider.DataDestination
                  && invocation.Status == expectedStatus
                  && !string.IsNullOrWhiteSpace(invocation.PromptId)
                  && !string.IsNullOrWhiteSpace(invocation.PromptVersion)
                  && !string.IsNullOrWhiteSpace(invocation.PromptContentHash)
                  && invocation.StartedAtUtc != default
                  && invocation.CompletedAtUtc is not null;
    if (requireProviderMetadata)
    {
        matches = matches
                  && invocation.Usage is not null
                  && !string.IsNullOrWhiteSpace(invocation.ProviderRequestId)
                  && string.IsNullOrWhiteSpace(invocation.FailureCode);
    }
    else
    {
        matches = matches
                  && invocation.Usage is null
                  && string.IsNullOrWhiteSpace(invocation.ProviderRequestId)
                  && invocation.FailureCode == "cancelled";
    }

    RequireR3(matches, "r3_invocation_mismatch");
    return invocation;
}

static async Task<AiSettingsDto> WaitForDeepSeekConfigurationOnlyAsync(
    IDesktopApiClient api,
    CancellationToken cancellationToken)
{
    while (true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = await api.GetAiSettingsAsync(cancellationToken);
        var provider = settings.Providers.Single(item => item.ProviderId == "deepseek");
        var modelId = settings.CurrentChatRoute.ModelId;
        if (provider.ConfigurationState == "Configured"
            && settings.CurrentChatRoute.ProviderId == "deepseek"
            && modelId is DeepSeekChatModelProvider.FlashModelId
                or DeepSeekChatModelProvider.ProModelId)
        {
            return settings;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
    }
}

static void RequireR3(bool condition, string errorCode)
{
    if (!condition)
    {
        throw new R3ValidationFailureException(errorCode);
    }
}

static string SafeR3ErrorCode(Exception exception) => exception switch
{
    R3ValidationFailureException failure => failure.Code,
    R3ValidationBudgetExceededException budget => budget.Code,
    OperationCanceledException => "r3_total_timeout",
    ChatModelException chat => chat.Error.Code,
    _ => "r3_validation_failed"
};

static async Task RunStage2LiveProviderAcceptanceAsync(
    IDesktopApiClient api,
    string projectRoot,
    List<object> results)
{
    const string codexProvider = "codex";
    const string codexModel = "codex-default";
    const string deepSeekProvider = "deepseek";

    var initialSettings = await api.GetAiSettingsAsync();
    if (initialSettings.ProgrammingAgent != "Codex"
        || !initialSettings.Providers.Any(provider => provider.ProviderId == codexProvider)
        || !initialSettings.Providers.Any(provider => provider.ProviderId == deepSeekProvider))
    {
        throw new InvalidOperationException("真实双 Provider 验收缺少 Codex、DeepSeek 或独立编程 Agent。 ");
    }

    await api.SetChatRouteAsync(new SetChatRouteRequestDto(codexProvider, codexModel));
    var session = await api.StartNewSessionAsync("阶段二真实双 Provider 验收");
    var codexToken = $"青桥-{Guid.NewGuid():N}";
    var codexPrompts = new[]
    {
        $"请记住校验词“{codexToken}”。再给出两个编号方案：第一个叫青桥，第二个叫赤塔。只回答“已记录”。",
        "第二个方案叫什么？只回答方案名。",
        "刚才那个方案的第一步是什么？回答必须包含方案名。",
        "继续，给它补一个风险。回答必须包含方案名。",
        "不是这个，我说的是第二个方案。只回答它的方案名。"
    };
    var codexReplies = await RunLiveConversationTurnsAsync(
        api,
        session.SessionId,
        codexPrompts,
        "codex");
    var codexContinuityPassed = codexReplies[1].Contains("赤塔", StringComparison.Ordinal)
                                && codexReplies[2].Contains("赤塔", StringComparison.Ordinal)
                                && codexReplies[3].Contains("赤塔", StringComparison.Ordinal)
                                && codexReplies[4].Contains("赤塔", StringComparison.Ordinal);
    AddStage2Result(
        results,
        1,
        "real Codex multi-turn references and correction",
        codexContinuityPassed,
        "RealCodexFiveTurns",
        codexReplies[^1]);
    if (!codexContinuityPassed)
    {
        throw new InvalidOperationException("真实 Codex 五轮连续指代没有通过。 ");
    }

    await RunLiveInterruptionAsync(api, session.SessionId, "codex", results, 2);

    Console.WriteLine("[stage2 live] WAITING_FOR_DEEPSEEK_UI_CONFIGURATION");
    Console.WriteLine("[stage2 live] Enter the DeepSeek API Key only in the visible YuanShu settings page, select DeepSeek V4 Flash, save, and check the connection.");
    Console.Out.Flush();
    var health = await WaitForDeepSeekUiConfigurationAsync(api, TimeSpan.FromMinutes(20));
    AddStage2Result(
        results,
        3,
        "real DeepSeek credential, route and official endpoint health",
        health.State == "Healthy",
        "VisibleUiConfigurationAndRealNetwork",
        health.SafeMessage);

    var deepSeekToken = $"银舟-{Guid.NewGuid():N}";
    var deepSeekPrompts = new[]
    {
        $"请记住新校验词“{deepSeekToken}”。给出两个编号方案：第一个叫银舟，第二个叫墨塔。只回答“已记录”。",
        "第二个方案叫什么？只回答方案名。",
        "刚才那个方案的第一步是什么？回答必须包含方案名。",
        "继续，给它补一个风险。回答必须包含方案名。",
        "不是这个，我说的是第二个方案。只回答它的方案名。"
    };
    var deepSeekReplies = await RunLiveConversationTurnsAsync(
        api,
        session.SessionId,
        deepSeekPrompts,
        "deepseek");
    var deepSeekContinuityPassed = deepSeekReplies[1].Contains("墨塔", StringComparison.Ordinal)
                                   && deepSeekReplies[2].Contains("墨塔", StringComparison.Ordinal)
                                   && deepSeekReplies[3].Contains("墨塔", StringComparison.Ordinal)
                                   && deepSeekReplies[4].Contains("墨塔", StringComparison.Ordinal);
    AddStage2Result(
        results,
        4,
        "real DeepSeek multi-turn references and correction",
        deepSeekContinuityPassed,
        "RealDeepSeekFiveTurns",
        deepSeekReplies[^1]);
    if (!deepSeekContinuityPassed)
    {
        throw new InvalidOperationException("真实 DeepSeek 五轮连续指代没有通过。 ");
    }

    await RunLiveInterruptionAsync(api, session.SessionId, "deepseek", results, 5);

    var project = await api.AddProjectAsync(new AddProjectRequestDto(
        projectRoot,
        "阶段二真实编程 Agent 回归"));
    var programming = await api.CreateTaskAsync(new CreateTaskRequestDto(
        project.Id,
        """
        新建 stage2-programming-regression.txt，内容只写 stage2-codex-agent-ok。
        不要修改其他文件。必须实际运行 dotnet test Acceptance.csproj --nologo --no-restore，并如实返回结果。
        """,
        "DeepSeek 普通聊天下的 Codex 编程回归"));
    var programmingDone = await WaitForTerminalEvidenceAsync(
        api,
        programming.TaskId,
        TimeSpan.FromMinutes(3));
    var programmingPath = Path.Combine(projectRoot, "stage2-programming-regression.txt");
    var programmingPassed = programmingDone.Summary.Status == "Succeeded"
                            && programmingDone.Evidence?.VerificationStatus == "Verified"
                            && programmingDone.Evidence.TestStatus == "Passed"
                            && File.Exists(programmingPath)
                            && await File.ReadAllTextAsync(programmingPath) == "stage2-codex-agent-ok";
    AddStage2Result(
        results,
        6,
        "real Codex programming agent while ordinary chat uses DeepSeek",
        programmingPassed,
        "IndependentProgrammingAgentLifecycle",
        programmingDone.Summary.Status);
    if (!programmingPassed)
    {
        throw new InvalidOperationException("DeepSeek 普通聊天下的真实 Codex 编程回归未通过。 ");
    }

    await api.SetChatRouteAsync(new SetChatRouteRequestDto(codexProvider, codexModel));
    var returnToCodex = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
        "现在已经切回最初的聊天 Provider。只回答我先后让你记住的两个校验词，中间用顿号分隔。",
        "Text",
        $"stage2-return-codex-{Guid.NewGuid():N}",
        session.SessionId));
    var finalSnapshot = await WaitForSessionTurnAsync(
        api,
        returnToCodex.TurnId,
        ["Completed"],
        TimeSpan.FromMinutes(2));
    var finalReply = finalSnapshot.Turns.Single(turn => turn.Id == returnToCodex.TurnId)
        .ResultSummary ?? string.Empty;
    var routeBack = await api.GetAiSettingsAsync();
    var switchPassed = routeBack.CurrentChatRoute.ProviderId == codexProvider
                       && finalSnapshot.SessionId == session.SessionId
                       && finalReply.Contains(codexToken, StringComparison.Ordinal)
                       && finalReply.Contains(deepSeekToken, StringComparison.Ordinal);
    AddStage2Result(
        results,
        7,
        "real same-Session Codex to DeepSeek to Codex switch",
        switchPassed,
        "ProviderAtoBtoAWithPersistedHistory",
        finalReply);
    if (!switchPassed)
    {
        throw new InvalidOperationException("真实 Codex→DeepSeek→Codex 同 Session 切换未通过。 ");
    }
}

static async Task<IReadOnlyList<string>> RunLiveConversationTurnsAsync(
    IDesktopApiClient api,
    Guid sessionId,
    IReadOnlyList<string> prompts,
    string providerId)
{
    var replies = new List<string>(prompts.Count);
    for (var index = 0; index < prompts.Count; index++)
    {
        var submitted = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
            prompts[index],
            "Text",
            $"stage2-{providerId}-turn-{index + 1:D2}-{Guid.NewGuid():N}",
            sessionId));
        var snapshot = await WaitForSessionTurnAsync(
            api,
            submitted.TurnId,
            ["Completed"],
            TimeSpan.FromMinutes(2));
        replies.Add(snapshot.Turns.Single(turn => turn.Id == submitted.TurnId).ResultSummary
                    ?? string.Empty);
        Console.WriteLine($"[stage2 {providerId} conversation {index + 1:D2}/{prompts.Count:D2}] completed");
    }

    return replies;
}

static async Task RunLiveInterruptionAsync(
    IDesktopApiClient api,
    Guid sessionId,
    string providerId,
    List<object> results,
    int resultNumber)
{
    var oldMarker = $"OLD-{providerId.ToUpperInvariant()}-{Guid.NewGuid():N}";
    var before = await api.GetCurrentSessionAsync()
                 ?? throw new InvalidOperationException("打断测试前 Session 不存在。 ");
    var assistantCountBefore = before.Messages.Count(message => message.Role == "Assistant");
    var old = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
        $"请写一篇至少两万字的长回答，每一段都包含标记“{oldMarker}”，不要提前结束。",
        "Text",
        $"stage2-{providerId}-interrupt-old-{Guid.NewGuid():N}",
        sessionId));
    await WaitForSessionTurnAsync(api, old.TurnId, ["Responding"], TimeSpan.FromSeconds(30));
    await Task.Delay(TimeSpan.FromMilliseconds(750));
    var replacementMarker = $"NEW-{providerId.ToUpperInvariant()}-{Guid.NewGuid():N}";
    var replacement = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
        $"停，先别说这个。只回答“{replacementMarker}”。",
        "Text",
        $"stage2-{providerId}-interrupt-new-{Guid.NewGuid():N}",
        sessionId));
    var replacementDone = await WaitForSessionTurnAsync(
        api,
        replacement.TurnId,
        ["Completed"],
        TimeSpan.FromMinutes(2));
    await Task.Delay(TimeSpan.FromSeconds(10));
    var afterDelay = await api.GetCurrentSessionAsync()
                     ?? throw new InvalidOperationException("打断测试后 Session 不存在。 ");
    var oldTurn = afterDelay.Turns.Single(turn => turn.Id == old.TurnId);
    var replacementTurn = replacementDone.Turns.Single(turn => turn.Id == replacement.TurnId);
    var interruptionPassed = oldTurn.Phase == "Cancelled"
                             && replacementTurn.Phase == "Completed"
                             && replacementTurn.ResultSummary?.Contains(
                                 replacementMarker,
                                 StringComparison.Ordinal) == true
                             && afterDelay.Messages.Count(message => message.Role == "Assistant")
                             == assistantCountBefore + 1
                             && !afterDelay.Messages.Any(message =>
                                 message.Role == "Assistant"
                                 && message.Content.Contains(oldMarker, StringComparison.Ordinal));
    AddStage2Result(
        results,
        resultNumber,
        $"real {providerId} cancellation and late-result guard",
        interruptionPassed,
        "ProviderCancellationAndLateResultGuard",
        replacementTurn.ResultSummary ?? string.Empty);
    if (!interruptionPassed)
    {
        throw new InvalidOperationException($"真实 {providerId} 打断没有通过。 ");
    }
}

static async Task<AiProviderHealthDto> WaitForDeepSeekUiConfigurationAsync(
    IDesktopApiClient api,
    TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    var nextHealthCheck = DateTimeOffset.MinValue;
    AiProviderHealthDto? latestHealth = null;
    while (DateTimeOffset.UtcNow < deadline)
    {
        var settings = await api.GetAiSettingsAsync();
        var provider = settings.Providers.Single(item => item.ProviderId == "deepseek");
        if (provider.ConfigurationState == "Configured"
            && settings.CurrentChatRoute.ProviderId == "deepseek"
            && settings.CurrentChatRoute.ModelId == "deepseek-v4-flash")
        {
            if (provider.Health.State == "Healthy")
            {
                return provider.Health;
            }

            if (DateTimeOffset.UtcNow >= nextHealthCheck)
            {
                latestHealth = await api.CheckAiProviderHealthAsync(
                    new ProviderIdRequestDto("deepseek"));
                if (latestHealth.State == "Healthy")
                {
                    return latestHealth;
                }

                nextHealthCheck = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
                Console.WriteLine($"[stage2 live] DeepSeek health={latestHealth.State}");
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    throw new TimeoutException(
        $"等待 DeepSeek 可见设置、V4 Flash 路由和真实健康检查超时；最后状态：{latestHealth?.State ?? "NotConfigured"}。 ");
}

static void AddStage2Result(
    List<object> results,
    int number,
    string scenario,
    bool passed,
    string verification,
    string userSummary)
{
    results.Add(new
    {
        Number = number,
        Scenario = scenario,
        Status = passed ? "Succeeded" : "Failed",
        Verification = verification,
        ThreadId = (string?)null,
        CurrentAttempt = 1,
        Passed = passed,
        UserSummary = userSummary
    });
    Console.WriteLine($"[stage2 {number}/7] {scenario}, pass={passed}");
}

static async Task RunStage1SessionAcceptanceAsync(
    IDesktopApiClient api,
    string projectRoot,
    List<object> results)
{
    var validationWord = $"蓝鹭-{Guid.NewGuid():N}";
    var session = await api.StartNewSessionAsync("阶段一真实十轮连续对话");
    var prompts = new[]
    {
        $"请记住校验词“{validationWord}”。我们正在设计一款 AI 助手，第一步先统一会话状态。只回答“已记录”。",
        "为这款助手给出两个编号方案：方案一叫青桥，方案二叫赤塔。每个方案只写一步。",
        "第二个详细一点。开头必须写“赤塔展开”。",
        "刚才那个方案最大的风险是什么？回答中必须写出方案名。",
        "继续，补充一个降低这个风险的办法。",
        "不是这个，我说的是第二个方案的第一步。只回答方案名和第一步。",
        "你前面说过“先统一会话状态”。请用一个生活化例子解释这句话。",
        "我最开始让你记住的校验词是什么？只回答校验词。",
        "把刚才关于赤塔方案的结论压缩成两点，并说明它和统一会话状态的关系。",
        "继续，用三句话总结我们现在正在讨论什么、已经决定什么、下一步是什么。"
    };
    var replies = new List<string>();
    for (var index = 0; index < prompts.Length; index++)
    {
        var submitted = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
            prompts[index],
            "Text",
            $"stage1-real-turn-{index + 1:D2}-{Guid.NewGuid():N}",
            session.SessionId));
        var snapshot = await WaitForSessionTurnAsync(
            api,
            submitted.TurnId,
            ["Completed"],
            TimeSpan.FromMinutes(2));
        var turn = snapshot.Turns.Single(item => item.Id == submitted.TurnId);
        replies.Add(turn.ResultSummary ?? string.Empty);
        Console.WriteLine($"[stage1 conversation {index + 1:D2}/10] {turn.Phase}");
    }

    var tenTurns = await api.GetCurrentSessionAsync()
        ?? throw new InvalidOperationException("真实十轮会话已经不存在。 ");
    var continuityPassed = tenTurns.SessionId == session.SessionId
                           && tenTurns.Turns.Count == 10
                           && tenTurns.Messages.Count == 20
                           && tenTurns.Turns.All(turn => turn.Phase == "Completed")
                           && replies[2].Contains("赤塔", StringComparison.Ordinal)
                           && replies[3].Contains("赤塔", StringComparison.Ordinal)
                           && replies[5].Contains("赤塔", StringComparison.Ordinal)
                           && replies[7].Contains(validationWord, StringComparison.Ordinal)
                           && replies[8].Contains("赤塔", StringComparison.Ordinal);
    results.Add(new
    {
        Number = 1,
        Scenario = "real Session Coordinator ten-turn semantic continuity",
        Status = continuityPassed ? "Succeeded" : "Failed",
        Verification = "RealProviderTenTurns",
        ThreadId = (string?)null,
        CurrentAttempt = 10,
        Passed = continuityPassed,
        UserSummary = replies[^1]
    });
    if (!continuityPassed)
    {
        throw new InvalidOperationException(
            $"真实十轮语义连续性未通过：{JsonSerializer.Serialize(replies)}");
    }
    Console.WriteLine("[stage1 continuity] passed=10/10");

    var interruptedTurns = new List<Guid>();
    for (var attempt = 1; attempt <= 3; attempt++)
    {
        var old = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
            $"请写一篇至少八千字的长回答，逐段解释会话协调的 {attempt} 个方面，不要提前总结。",
            "Text",
            $"stage1-real-interrupt-old-{attempt}-{Guid.NewGuid():N}",
            session.SessionId));
        await WaitForSessionTurnAsync(
            api,
            old.TurnId,
            ["Responding"],
            TimeSpan.FromSeconds(20));
        var replacement = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
            $"停，先别说这个。第 {attempt} 次新指令，只回答“新指令{attempt}已接收”。",
            "Text",
            $"stage1-real-interrupt-new-{attempt}-{Guid.NewGuid():N}",
            session.SessionId));
        var replacementDone = await WaitForSessionTurnAsync(
            api,
            replacement.TurnId,
            ["Completed"],
            TimeSpan.FromMinutes(2));
        var oldTurn = replacementDone.Turns.Single(turn => turn.Id == old.TurnId);
        if (oldTurn.Phase != "Cancelled"
            || replacementDone.Messages.Any(message =>
                message.Role == "Assistant"
                && message.Content.Contains("八千字", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"第 {attempt} 次真实插话未能停止旧回答。 ");
        }

        interruptedTurns.Add(old.TurnId);
        Console.WriteLine($"[stage1 interrupt {attempt}/3] old=Cancelled, replacement=Completed");
    }

    await Task.Delay(TimeSpan.FromSeconds(10));
    var afterDelay = await api.GetCurrentSessionAsync()
        ?? throw new InvalidOperationException("打断验收后的会话不存在。 ");
    var interruptionPassed = interruptedTurns.All(turnId =>
                                 afterDelay.Turns.Single(turn => turn.Id == turnId).Phase == "Cancelled")
                             && afterDelay.Messages.Count(message => message.Role == "Assistant") == 13;
    results.Add(new
    {
        Number = 2,
        Scenario = "three real provider interruptions with no late reply",
        Status = interruptionPassed ? "Succeeded" : "Failed",
        Verification = "ProviderCancellationAndLateResultGuard",
        ThreadId = (string?)null,
        CurrentAttempt = 3,
        Passed = interruptionPassed,
        UserSummary = "连续三次插话后等待 10 秒，旧回答均未重新出现。"
    });
    if (!interruptionPassed)
    {
        throw new InvalidOperationException("真实 Provider 连续三次打断验收未通过。 ");
    }
    Console.WriteLine("[stage1 interrupts] passed=3/3, lateReply=false");

    var project = await api.AddProjectAsync(new AddProjectRequestDto(projectRoot, "阶段一真实项目补充"));
    var projectRequest = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
        "帮我修改这个项目：新增 stage1-project.txt，内容只写 stage1-project-ok，并运行 dotnet test Acceptance.csproj --nologo --no-restore。",
        "Text",
        $"stage1-real-project-{Guid.NewGuid():N}",
        session.SessionId));
    await WaitForSessionTurnAsync(api, projectRequest.TurnId, ["WaitingForProject"], TimeSpan.FromSeconds(10));
    var withProject = await api.ProvideSessionProjectAsync(session.SessionId, projectRequest.TurnId, project.Id);
    if (withProject.Turns.Single(turn => turn.Id == projectRequest.TurnId).Phase != "WaitingForConfirmation")
    {
        throw new InvalidOperationException("真实项目补充后没有续接原任务。 ");
    }

    await api.ConfirmSessionTurnAsync(session.SessionId, projectRequest.TurnId, confirmed: true);
    var projectDone = await WaitForSessionTurnAsync(
        api,
        projectRequest.TurnId,
        ["Completed"],
        TimeSpan.FromMinutes(3));
    var projectPassed = File.Exists(Path.Combine(projectRoot, "stage1-project.txt"))
                        && projectDone.Turns.Single(turn => turn.Id == projectRequest.TurnId).TaskId is not null;
    results.Add(new
    {
        Number = 3,
        Scenario = "real missing-project continuation",
        Status = projectPassed ? "Succeeded" : "Failed",
        Verification = "SameOriginalTurnAndRealTask",
        ThreadId = (string?)null,
        CurrentAttempt = 1,
        Passed = projectPassed,
        UserSummary = projectDone.Turns.Single(turn => turn.Id == projectRequest.TurnId).ResultSummary ?? string.Empty
    });
    if (!projectPassed)
    {
        throw new InvalidOperationException("真实项目补充流程未通过。 ");
    }
    Console.WriteLine("[stage1 project context] passed");

    var selectedFile = Path.Combine(projectRoot, "stage1-selected-file.txt");
    await File.WriteAllTextAsync(selectedFile, "stage1 selected file");
    var fileRequest = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
        "帮我打开这个文件",
        "Text",
        $"stage1-real-file-{Guid.NewGuid():N}",
        session.SessionId));
    await WaitForSessionTurnAsync(api, fileRequest.TurnId, ["WaitingForFile"], TimeSpan.FromSeconds(10));
    var withFile = await api.ProvideSessionFileAsync(session.SessionId, fileRequest.TurnId, selectedFile);
    if (withFile.Turns.Single(turn => turn.Id == fileRequest.TurnId).Phase != "WaitingForConfirmation")
    {
        throw new InvalidOperationException("真实文件补充后没有续接原任务。 ");
    }

    var fileDone = await api.ConfirmSessionTurnAsync(session.SessionId, fileRequest.TurnId, confirmed: true);
    var filePassed = fileDone.Turns.Single(turn => turn.Id == fileRequest.TurnId).Phase == "Completed";
    results.Add(new
    {
        Number = 4,
        Scenario = "real missing-file continuation",
        Status = filePassed ? "Succeeded" : "Failed",
        Verification = "VisibleConfirmationBeforeLaunch",
        ThreadId = (string?)null,
        CurrentAttempt = 1,
        Passed = filePassed,
        UserSummary = fileDone.Turns.Single(turn => turn.Id == fileRequest.TurnId).ResultSummary ?? string.Empty
    });
    if (!filePassed)
    {
        throw new InvalidOperationException("真实文件补充流程未通过。 ");
    }
    Console.WriteLine("[stage1 file context] passed");

    using var notepad = await StartVisibleNotepadAsync(selectedFile);
    await ActivateExactForegroundWindowAsync(notepad);
    var windowReject = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
        "看看这个窗口是什么",
        "Text",
        $"stage1-real-window-reject-{Guid.NewGuid():N}",
        session.SessionId));
    await WaitForSessionTurnAsync(
        api,
        windowReject.TurnId,
        ["WaitingForWindowConsent"],
        TimeSpan.FromSeconds(10));
    var rejected = await api.RespondSessionWindowConsentAsync(
        session.SessionId,
        windowReject.TurnId,
        granted: false);
    var rejectedTurn = rejected.Turns.Single(turn => turn.Id == windowReject.TurnId);
    var rejectedPassed = rejectedTurn.Phase == "Cancelled"
                         && rejectedTurn.WindowHandle == notepad.MainWindowHandle.ToInt64()
                         && rejectedTurn.WindowTitle?.Contains(
                             Path.GetFileName(selectedFile),
                             StringComparison.OrdinalIgnoreCase) == true;

    await ActivateExactForegroundWindowAsync(notepad);
    var windowApprove = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
        "看看这个窗口是什么",
        "Text",
        $"stage1-real-window-approve-{Guid.NewGuid():N}",
        session.SessionId));
    await WaitForSessionTurnAsync(
        api,
        windowApprove.TurnId,
        ["WaitingForWindowConsent"],
        TimeSpan.FromSeconds(10));
    var approved = await api.RespondSessionWindowConsentAsync(
        session.SessionId,
        windowApprove.TurnId,
        granted: true);
    var approvedTurn = approved.Turns.Single(turn => turn.Id == windowApprove.TurnId);
    var windowPassed = rejectedPassed
                       && approvedTurn.Phase == "Completed"
                       && approvedTurn.WindowHandle == notepad.MainWindowHandle.ToInt64()
                       && approvedTurn.WindowTitle?.Contains(
                           Path.GetFileName(selectedFile),
                           StringComparison.OrdinalIgnoreCase) == true
                       && !string.IsNullOrWhiteSpace(approvedTurn.ResultSummary);
    results.Add(new
    {
        Number = 5,
        Scenario = "real single-window consent approve and reject",
        Status = windowPassed ? "Succeeded" : "Failed",
        Verification = "SingleWindowLocalCapture",
        ThreadId = (string?)null,
        CurrentAttempt = 2,
        Passed = windowPassed,
        UserSummary = approvedTurn.ResultSummary ?? string.Empty
    });
    if (!windowPassed)
    {
        throw new InvalidOperationException("真实窗口授权同意/拒绝流程未通过。 ");
    }

    notepad.CloseMainWindow();
}

static string? GetOption(string[] arguments, string prefix) =>
    arguments.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        ?[prefix.Length..];

static async Task<SessionSnapshotDto> WaitForSessionTurnAsync(
    IDesktopApiClient api,
    Guid turnId,
    string[] expectedPhases,
    TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    SessionSnapshotDto? snapshot = null;
    Exception? lastTransientFailure = null;
    while (DateTimeOffset.UtcNow < deadline)
    {
        try
        {
            snapshot ??= await api.GetCurrentSessionAsync();
            var turn = snapshot?.Turns.SingleOrDefault(item => item.Id == turnId);
            if (turn?.Phase is "Failed" or "Cancelled" or "Interrupted"
                && !expectedPhases.Contains(turn.Phase))
            {
                throw new InvalidOperationException(
                    $"统一会话进入非预期终态：session={snapshot!.SessionId}, "
                    + $"turn={turn.Id}, phase={turn.Phase}, version={snapshot.ChangeVersion}, "
                    + $"detail={turn.FailureMessage ?? turn.ResultSummary ?? "无"}。 ");
            }

            if (turn is not null && expectedPhases.Contains(turn.Phase))
            {
                return snapshot!;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            var waitMilliseconds = (int)Math.Clamp(remaining.TotalMilliseconds, 1, 5_000);
            using var callTimeout = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(waitMilliseconds + 5_000));
            snapshot = await api.WaitForSessionUpdateAsync(
                snapshot?.ChangeVersion ?? -1,
                waitMilliseconds,
                callTimeout.Token);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or OperationCanceledException)
        {
            lastTransientFailure = exception;
            snapshot = null;
            await Task.Delay(250);
        }
    }

    throw new TimeoutException("统一会话真实验收等待超时。 ", lastTransientFailure);
}

static async Task<Process> StartVisibleNotepadAsync(string filePath)
{
    var startedAt = DateTimeOffset.Now.AddSeconds(-2);
    var launched = Process.Start(new ProcessStartInfo
    {
        FileName = "notepad.exe",
        UseShellExecute = false,
        ArgumentList = { filePath }
    }) ?? throw new InvalidOperationException("真实验收记事本窗口未启动。 ");
    Process? visible = null;
    await WaitAsync(() =>
    {
        launched.Refresh();
        if (!launched.HasExited && launched.MainWindowHandle != IntPtr.Zero)
        {
            visible = launched;
            return Task.FromResult(true);
        }

        var fileName = Path.GetFileName(filePath);
        visible = Process.GetProcessesByName("notepad")
            .Where(process =>
            {
                try
                {
                    process.Refresh();
                    return !process.HasExited
                           && process.StartTime >= startedAt.LocalDateTime
                           && process.MainWindowHandle != IntPtr.Zero
                           && process.MainWindowTitle.Contains(fileName, StringComparison.OrdinalIgnoreCase);
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            })
            .OrderByDescending(process => process.StartTime)
            .FirstOrDefault();
        return Task.FromResult(visible is not null);
    }, TimeSpan.FromSeconds(15));

    if (!ReferenceEquals(visible, launched))
    {
        launched.Dispose();
    }

    return visible!;
}

static async Task ActivateExactForegroundWindowAsync(Process process)
{
    await WaitAsync(() =>
    {
        process.Refresh();
        if (process.HasExited || process.MainWindowHandle == IntPtr.Zero)
        {
            return Task.FromResult(false);
        }

        _ = VisibleWindowActivation.TryActivate(process.MainWindowHandle, TimeSpan.FromSeconds(1));
        return Task.FromResult(NativeMethods.GetForegroundWindow() == process.MainWindowHandle);
    }, TimeSpan.FromSeconds(15));

    await Task.Delay(300);
    process.Refresh();
    if (process.HasExited
        || process.MainWindowHandle == IntPtr.Zero
        || NativeMethods.GetForegroundWindow() != process.MainWindowHandle)
    {
        throw new InvalidOperationException("真实验收未能把指定记事本窗口保持在前台。 ");
    }
}

static void TryCloseAcceptanceProcess(int processId)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        if (!process.ProcessName.Contains("notepad", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!process.CloseMainWindow())
        {
            process.Kill(entireProcessTree: false);
        }
    }
    catch (ArgumentException)
    {
        // The launched process can exit or hand off before cleanup.
    }
    catch (InvalidOperationException)
    {
        // No remaining acceptance process needs cleanup.
    }
}

static object ToResult(int number, string scenario, TaskDetailsDto details, bool passed) => new
{
    Number = number,
    Scenario = scenario,
    Status = details.Summary.Status,
    Verification = details.Evidence?.VerificationStatus,
    details.ThreadId,
    details.CurrentAttempt,
    Passed = passed,
    details.Summary.UserSummary
};

static async Task<TaskDetailsDto> WaitForTerminalEvidenceAsync(
    IDesktopApiClient api,
    Guid taskId,
    TimeSpan timeout)
{
    TaskDetailsDto? details = null;
    await WaitAsync(
        async () =>
        {
            details = await api.GetTaskAsync(taskId);
            return details?.Summary.Status is "Succeeded" or "Failed"
                   && details.Evidence is not null;
        },
        timeout);
    return details!;
}

static async Task<ConversationDetailsDto> WaitForConversationAsync(
    IDesktopApiClient api,
    Guid conversationId,
    int expectedMessageCount,
    TimeSpan timeout)
{
    ConversationDetailsDto? details = null;
    await WaitAsync(
        async () =>
        {
            details = await api.GetConversationAsync(conversationId);
            if (details?.Summary.Status is "Failed" or "Interrupted")
            {
                throw new InvalidOperationException(
                    details.Summary.FailureMessage ?? "真实对话执行失败。 ");
            }

            return details?.Summary.Status == "Ready"
                   && details.Messages.Count >= expectedMessageCount;
        },
        timeout);
    return details!;
}

static async Task<TaskDetailsDto> WaitForStatusAsync(
    IDesktopApiClient api,
    Guid taskId,
    string expected,
    TimeSpan timeout)
{
    TaskDetailsDto? details = null;
    await WaitAsync(
        async () =>
        {
            details = await api.GetTaskAsync(taskId);
            return details?.Summary.Status == expected;
        },
        timeout);
    return details!;
}

static async Task WaitAsync(Func<Task<bool>> condition, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    Exception? lastTransientFailure = null;
    while (DateTimeOffset.UtcNow < deadline)
    {
        try
        {
            if (await condition())
            {
                return;
            }
        }
        catch (Exception exception) when (exception is TimeoutException or IOException)
        {
            lastTransientFailure = exception;
        }

        await Task.Delay(250);
    }

    throw new TimeoutException("Desktop V0.1 真实验收等待超时。 ", lastTransientFailure);
}

static int DeliveredNotificationCount(string dataRoot)
{
    try
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dataRoot, "client-settings.json")));
        return document.RootElement.GetProperty("deliveredNotificationKeys").GetArrayLength();
    }
    catch
    {
        return 0;
    }
}

static bool HasDeliveredNotification(
    string dataRoot,
    Guid taskId,
    string status)
{
    try
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dataRoot, "client-settings.json")));
        var prefix = $"{taskId:N}:{status}:";
        return document.RootElement
            .GetProperty("deliveredNotificationKeys")
            .EnumerateArray()
            .Any(item => item.GetString()?.StartsWith(prefix, StringComparison.Ordinal) == true);
    }
    catch
    {
        return false;
    }
}

static async Task PrepareProjectAsync(string root)
{
    await File.WriteAllTextAsync(
        Path.Combine(root, "Acceptance.csproj"),
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
            <PackageReference Include="xunit" Version="2.9.3" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
          </ItemGroup>
        </Project>
        """);
    await File.WriteAllTextAsync(
        Path.Combine(root, "SmokeTests.cs"),
        "using Xunit; public sealed class SmokeTests { [Fact] public void Passes() => Assert.True(true); }\n");
    await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "bin/\nobj/\n");
    await RunAsync(root, "dotnet", "restore", "Acceptance.csproj", "--ignore-failed-sources");
    await RunAsync(root, "git", "init", "--quiet");
    await RunAsync(root, "git", "add", ".");
    await RunAsync(root, "git", "-c", "user.name=ScreenGuide Test", "-c", "user.email=test@example.invalid", "commit", "--quiet", "-m", "baseline");
}

static async Task RunAsync(string root, string executable, params string[] arguments)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        }
    };
    foreach (var argument in arguments)
    {
        process.StartInfo.ArgumentList.Add(argument);
    }

    process.Start();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(await process.StandardError.ReadToEndAsync());
    }
}

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ScreenGuide.slnx")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();
}
