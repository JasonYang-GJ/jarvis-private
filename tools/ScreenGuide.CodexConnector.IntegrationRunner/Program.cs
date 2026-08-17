using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopHost.Runtime;
using AgentTaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
if (string.IsNullOrWhiteSpace(localData))
{
    throw new InvalidOperationException("无法确定 LocalApplicationData。 ");
}

var runRoot = Path.Combine(
    localData,
    "ScreenGuide",
    "Experiments",
    "2B1",
    DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
var projectRoot = Directory.CreateDirectory(Path.Combine(runRoot, "authorized-project")).FullName;
await InitializeGitProjectAsync(projectRoot);
var options = new DesktopHostOptions(Path.Combine(runRoot, "host-data"));
var results = new List<IntegrationResult>();

using var host = DesktopHostFactory.Build(Array.Empty<string>(), options);
await host.StartAsync();
var store = host.Services.GetRequiredService<ILocalTaskStore>();
var execution = host.Services.GetRequiredService<AgentTaskExecutionService>();
var device = host.Services.GetRequiredService<DesktopHostState>().Snapshot.LocalDevice
    ?? throw new InvalidOperationException("Host 没有初始化本机 Device。 ");
var now = DateTimeOffset.UtcNow;
var project = new ProjectRecord
{
    Id = Guid.NewGuid(),
    Name = "2B1 real integration project",
    RootPath = projectRoot,
    AuthorizationState = ProjectAuthorizationState.Authorized,
    AuthorizedByDeviceId = device.Id,
    AuthorizedAtUtc = now,
    CreatedAtUtc = now,
    UpdatedAtUtc = now
};
await store.SetProjectAuthorizationAsync(project);

for (var number = 1; number <= 18; number++)
{
    var expectedSummary = $"real-task-{number:D2}-completed";
    var task = await CreateTaskAsync(
        store,
        device,
        project,
        number,
        $"""
        这是 ScreenGuide CodexConnector 的本地生命周期验收任务 {number}/20。
        不要修改任何文件，不要运行任何命令，不要继续探索项目。
        请直接按已提供的 JSON Schema 返回：outcome 为 completed，summary 为 {expectedSummary}，changedFiles 为空数组，tests 为空数组，question 为 null，decisionOptions 为空数组。
        """);
    var startedAt = DateTimeOffset.UtcNow;
    var reference = await execution.StartTaskAsync(task.Id);
    await execution.WaitForTaskAsync(task.Id);
    results.Add(await ReadResultAsync(store, task, reference.ExternalRunId, startedAt, expectedSummary));
    WriteProgress(results[^1]);
}

{
    const int number = 19;
    var task = await CreateTaskAsync(
        store,
        device,
        project,
        number,
        """
        这是 ScreenGuide CodexConnector 的 action_required 验收任务 19/20。
        不要修改文件，不要运行命令。请直接按已提供的 JSON Schema 返回：outcome 为 action_required，summary 为 waiting-for-confirmation，changedFiles 和 tests 为空数组，question 为“请回复 GO”，decisionOptions 只包含 GO。
        """);
    var startedAt = DateTimeOffset.UtcNow;
    var first = await execution.StartTaskAsync(task.Id);
    await execution.WaitForTaskAsync(task.Id);
    var waiting = await store.GetTaskAsync(task.Id);
    var pending = await store.GetPendingDecisionRequestAsync(task.Id);
    var responseCommand = await RegisterCommandAsync(
        store,
        device,
        project,
        task.Id,
        CommandType.UserResponse,
        "{\"response\":\"GO\"}");
    var second = await execution.ContinueTaskAsync(
        task.Id,
        """
        GO。不要修改文件，不要运行命令。请直接按已提供的 JSON Schema 返回：outcome 为 completed，summary 为 action-required-resumed，changedFiles 和 tests 为空数组，question 为 null，decisionOptions 为空数组。
        """,
        responseCommand.Id);
    await execution.WaitForTaskAsync(task.Id);
    var result = await ReadResultAsync(
        store,
        task,
        second.ExternalRunId,
        startedAt,
        "action-required-resumed");
    var attempts = await store.GetAgentAttemptsAsync(task.Id);
    result = result with
    {
        Passed = result.Passed
            && waiting?.Status == AgentTaskStatus.WaitingForUser
            && pending is not null
            && first.ExternalRunId == second.ExternalRunId
            && attempts.Count == 2,
        Scenario = "action_required + same-thread resume"
    };
    results.Add(result);
    WriteProgress(result);
}

{
    const int number = 20;
    var marker = Path.Combine(projectRoot, "must-not-exist-after-cancel.txt");
    var task = await CreateTaskAsync(
        store,
        device,
        project,
        number,
        $"""
        这是 ScreenGuide CodexConnector 的真实取消验收任务 20/20。
        请立即运行下面这条命令并等待它结束：
        powershell -NoProfile -Command "Start-Sleep -Seconds 60; Set-Content -LiteralPath '{marker}' -Value 'cancel failed'"
        命令结束后再按 Schema 返回 completed。
        """);
    var startedAt = DateTimeOffset.UtcNow;
    var reference = await execution.StartTaskAsync(task.Id);
    var commandObserved = await WaitForCommandStartAsync(store, task.Id, TimeSpan.FromSeconds(45));
    var cancellationAccepted = await execution.CancelTaskAsync(task.Id, device.Id);
    await Task.Delay(TimeSpan.FromSeconds(3));
    var persisted = await store.GetTaskAsync(task.Id);
    var result = new IntegrationResult(
        number,
        "real cancellation + process tree",
        persisted?.Status.ToString() ?? "missing",
        reference.ExternalRunId,
        DateTimeOffset.UtcNow - startedAt,
        commandObserved
            && cancellationAccepted
            && persisted?.Status == AgentTaskStatus.Cancelled
            && !File.Exists(marker),
        null,
        (await store.GetAgentAttemptsAsync(task.Id)).Count,
        (await store.GetTaskEventsAsync(task.Id)).Count);
    results.Add(result);
    WriteProgress(result);
}

await host.StopAsync();
var completedCount = results.Count(item => item.Passed);
var falseCompletedCount = results.Count(item =>
    item.Status == AgentTaskStatus.Succeeded.ToString()
    && !item.Passed);
Console.WriteLine(JsonSerializer.Serialize(
    new
    {
        RuntimeRoot = runRoot,
        Total = results.Count,
        Passed = completedCount,
        Failed = results.Count - completedCount,
        FalseCompleted = falseCompletedCount,
        Results = results
    },
    new JsonSerializerOptions { WriteIndented = true }));
return completedCount == 20 && falseCompletedCount == 0 ? 0 : 1;

static async Task<AgentTask> CreateTaskAsync(
    ILocalTaskStore store,
    DeviceRecord device,
    ProjectRecord project,
    int number,
    string instruction)
{
    var command = await RegisterCommandAsync(
        store,
        device,
        project,
        null,
        CommandType.CreateTask,
        JsonSerializer.Serialize(new { instruction }));
    var now = DateTimeOffset.UtcNow;
    var task = new AgentTask
    {
        Id = Guid.NewGuid(),
        ProjectId = project.Id,
        CreatedByDeviceId = device.Id,
        Title = $"2B1 real task {number:D2}",
        Instruction = instruction,
        WorkingDirectoryRelativePath = ".",
        Executor = "codex",
        Status = AgentTaskStatus.Pending,
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
        Version = 0
    };
    await store.CreateTaskAsync(task, command.Id);
    return task;
}

static async Task InitializeGitProjectAsync(string projectRoot)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = projectRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }
    };
    process.StartInfo.ArgumentList.Add("init");
    process.StartInfo.ArgumentList.Add("--quiet");
    if (!process.Start())
    {
        throw new InvalidOperationException("无法初始化隔离的集成测试 Git 项目。 ");
    }

    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"无法初始化隔离的集成测试 Git 项目：{await process.StandardError.ReadToEndAsync()}");
    }
}

static async Task<CommandRecord> RegisterCommandAsync(
    ILocalTaskStore store,
    DeviceRecord device,
    ProjectRecord project,
    Guid? taskId,
    CommandType commandType,
    string payload)
{
    var now = DateTimeOffset.UtcNow;
    var command = new CommandRecord
    {
        Id = Guid.NewGuid(),
        SourceDeviceId = device.Id,
        ProjectId = project.Id,
        TaskId = taskId,
        IdempotencyKey = $"2b1-{Guid.NewGuid():N}",
        CommandType = commandType,
        PayloadJson = payload,
        ReceivedAtUtc = now,
        ExpiresAtUtc = now.AddMinutes(10),
        Status = CommandStatus.Received
    };
    var registration = await store.RegisterCommandAsync(command);
    if (!registration.Accepted)
    {
        throw new InvalidOperationException("集成测试 Command 被意外判定为重复。 ");
    }

    return registration.Command;
}

static async Task<IntegrationResult> ReadResultAsync(
    ILocalTaskStore store,
    AgentTask task,
    string? externalRunId,
    DateTimeOffset startedAt,
    string expectedSummary)
{
    var persisted = await store.GetTaskAsync(task.Id);
    var run = await store.GetAgentRunByTaskAsync(task.Id);
    var attempts = await store.GetAgentAttemptsAsync(task.Id);
    var events = await store.GetTaskEventsAsync(task.Id);
    var passed = persisted?.Status == AgentTaskStatus.Succeeded
        && !string.IsNullOrWhiteSpace(externalRunId)
        && externalRunId == run?.ExternalRunId
        && run.Status == AgentRunStatus.Succeeded
        && string.Equals(run.FinalSummary, expectedSummary, StringComparison.Ordinal)
        && events.Count(item => item.ToStatus == AgentTaskStatus.Succeeded) == 1;
    return new IntegrationResult(
        int.Parse(task.Title[^2..]),
        "authoritative success",
        persisted?.Status.ToString() ?? "missing",
        externalRunId,
        DateTimeOffset.UtcNow - startedAt,
        passed,
        run?.FinalSummary,
        attempts.Count,
        events.Count);
}

static async Task<bool> WaitForCommandStartAsync(
    ILocalTaskStore store,
    Guid taskId,
    TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        var events = await store.GetTaskEventsAsync(taskId);
        if (events.Any(item =>
                item.DataJson?.Contains("command_execution", StringComparison.Ordinal) == true
                && item.DataJson.Contains("item.started", StringComparison.Ordinal)))
        {
            return true;
        }

        if ((await store.GetTaskAsync(taskId))?.Status is
            AgentTaskStatus.Succeeded or AgentTaskStatus.Failed or AgentTaskStatus.Interrupted)
        {
            return false;
        }

        await Task.Delay(250);
    }

    return false;
}

static void WriteProgress(IntegrationResult result) => Console.WriteLine(
    $"[{result.Number:D2}/20] {result.Scenario}: {result.Status}, pass={result.Passed}, "
    + $"thread={result.ExternalRunId ?? "none"}, seconds={result.Duration.TotalSeconds:F1}");

internal sealed record IntegrationResult(
    int Number,
    string Scenario,
    string Status,
    string? ExternalRunId,
    TimeSpan Duration,
    bool Passed,
    string? Summary,
    int AttemptCount,
    int EventCount);
