using System.Diagnostics;
using System.Text.Json;
using ScreenGuide.DesktopProtocol;

var smokeOnly = args.Any(argument =>
    string.Equals(argument, "--smoke", StringComparison.OrdinalIgnoreCase));
var desktopActionSmoke = args.Any(argument =>
    string.Equals(argument, "--desktop-action-smoke", StringComparison.OrdinalIgnoreCase));
var conversationSmoke = args.Any(argument =>
    string.Equals(argument, "--conversation-smoke", StringComparison.OrdinalIgnoreCase));
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
var clientPath = Path.Combine(publishRoot, "ScreenGuide.DesktopClient.exe");
var hostPath = Path.Combine(publishRoot, "ScreenGuide.DesktopHost.exe");
if (!File.Exists(clientPath) || !File.Exists(hostPath))
{
    throw new FileNotFoundException("请先运行 scripts/build-desktop-release.ps1。 ");
}

var pipeName = $"ScreenGuide.DesktopV01.Real.{Guid.NewGuid():N}";
var startInfo = new ProcessStartInfo
{
    FileName = clientPath,
    WorkingDirectory = publishRoot,
    UseShellExecute = false
};
startInfo.ArgumentList.Add("--show");
startInfo.Environment["SCREEN_GUIDE_DATA_DIRECTORY"] = dataRoot;
startInfo.Environment["SCREEN_GUIDE_PIPE_NAME"] = pipeName;
startInfo.Environment["SCREEN_GUIDE_DESKTOP_HOST_PATH"] = hostPath;
using var client = Process.Start(startInfo)
    ?? throw new InvalidOperationException("最终 Release DesktopClient 未启动。 ");
var api = new DesktopApiClient(pipeName, TimeSpan.FromSeconds(3));
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
    if (!status.Codex.IsCompatible || status.Codex.Version != "0.147.0")
    {
        throw new InvalidOperationException(
            $"Codex 兼容门禁未通过：{status.Codex.Version ?? "not-found"}。 ");
    }

    if (conversationSmoke)
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

    var project = desktopActionSmoke || conversationSmoke
        ? null
        : await api.AddProjectAsync(new AddProjectRequestDto(
            projectRoot,
            "Desktop V0.1 real acceptance"));
    var regularTaskCount = desktopActionSmoke || conversationSmoke ? 0 : smokeOnly ? 1 : 18;
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

    if (!smokeOnly && !desktopActionSmoke && !conversationSmoke)
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
    if (!client.HasExited)
    {
        client.Kill(entireProcessTree: false);
        await client.WaitForExitAsync();
    }

    if (await api.PingAsync())
    {
        await api.ShutdownHostAsync();
    }
}

return 0;

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
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (await condition())
        {
            return;
        }

        await Task.Delay(250);
    }

    throw new TimeoutException("Desktop V0.1 真实验收等待超时。 ");
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
