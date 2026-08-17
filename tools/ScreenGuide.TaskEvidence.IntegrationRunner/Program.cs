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
    "2C",
    DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
var projectRoot = Directory.CreateDirectory(Path.Combine(runRoot, "authorized-project")).FullName;
await PrepareProjectAsync(projectRoot);
var options = new DesktopHostOptions(Path.Combine(runRoot, "host-data"));
using var host = DesktopHostFactory.Build(Array.Empty<string>(), options);
await host.StartAsync();
var store = host.Services.GetRequiredService<ILocalTaskStore>();
var execution = host.Services.GetRequiredService<AgentTaskExecutionService>();
var device = host.Services.GetRequiredService<DesktopHostState>().Snapshot.LocalDevice!;
var now = DateTimeOffset.UtcNow;
var project = new ProjectRecord
{
    Id = Guid.NewGuid(),
    Name = "2C evidence integration project",
    RootPath = projectRoot,
    AuthorizationState = ProjectAuthorizationState.Authorized,
    AuthorizedByDeviceId = device.Id,
    AuthorizedAtUtc = now,
    CreatedAtUtc = now,
    UpdatedAtUtc = now
};
await store.SetProjectAuthorizationAsync(project);
var results = new List<object>();

var verifiedTask = await CreateTaskAsync(
    store,
    device,
    project,
    "真实证据：修改并测试",
    """
    修复 Calculator.cs 中 Add 方法的错误，让现有测试通过。
    只修改 Calculator.cs，必须实际运行：dotnet test EvidenceSample.csproj --nologo
    完成后按输出 Schema 如实填写 changedFiles 和 tests。
    """);
await execution.StartTaskAsync(verifiedTask.Id);
await execution.WaitForTaskAsync(verifiedTask.Id);
var verifiedEvidence = RequireEvidence(await store.GetTaskEvidenceAsync(verifiedTask.Id));
results.Add(ToResult("file change + real test", verifiedEvidence));

var untestedTask = await CreateTaskAsync(
    store,
    device,
    project,
    "真实证据：没有测试",
    """
    新建 notes.txt，内容只写 evidence-ready。
    本任务不要运行任何测试。完成后按输出 Schema 如实填写 changedFiles，tests 为空数组。
    """);
await execution.StartTaskAsync(untestedTask.Id);
await execution.WaitForTaskAsync(untestedTask.Id);
var untestedEvidence = RequireEvidence(await store.GetTaskEvidenceAsync(untestedTask.Id));
results.Add(ToResult("file change + no test", untestedEvidence));

var actionTask = await CreateTaskAsync(
    store,
    device,
    project,
    "真实证据：等待后继续",
    """
    现在不要修改文件、不要运行命令。按输出 Schema 返回 action_required，问题为“是否创建 confirmed.txt 并运行测试？”，选项为 GO。
    """);
var first = await execution.StartTaskAsync(actionTask.Id);
await execution.WaitForTaskAsync(actionTask.Id);
var waitingStatus = (await store.GetTaskAsync(actionTask.Id))?.Status;
var evidenceWhileWaiting = await store.GetTaskEvidenceAsync(actionTask.Id);
var second = await execution.ContinueTaskAsync(
    actionTask.Id,
    """
    GO。新建 confirmed.txt，内容只写 confirmed，然后实际运行：dotnet test EvidenceSample.csproj --nologo
    完成后按输出 Schema 如实填写 changedFiles 和 tests。
    """);
await execution.WaitForTaskAsync(actionTask.Id);
var actionEvidence = RequireEvidence(await store.GetTaskEvidenceAsync(actionTask.Id));
results.Add(new
{
    Scenario = "action_required + same thread + verified finish",
    WaitingStatus = waitingStatus?.ToString(),
    EvidenceCreatedWhileWaiting = evidenceWhileWaiting is not null,
    SameThread = first.ExternalRunId == second.ExternalRunId,
    Evidence = ToResult("action-final", actionEvidence)
});

await host.StopAsync();
var passed = verifiedEvidence.VerificationStatus == EvidenceVerificationStatus.Verified
             && verifiedEvidence.Tests.Status == EvidenceTestStatus.Passed
             && verifiedEvidence.Git.ChangedFiles.Count == 1
             && untestedEvidence.VerificationStatus == EvidenceVerificationStatus.Unverified
             && untestedEvidence.Tests.Status == EvidenceTestStatus.NotRun
             && waitingStatus == AgentTaskStatus.WaitingForUser
             && evidenceWhileWaiting is null
             && first.ExternalRunId == second.ExternalRunId
             && actionEvidence.TaskStatus == AgentTaskStatus.Succeeded
             && actionEvidence.Git.ChangedFiles.Count == 1
             && (actionEvidence.Tests.Status == EvidenceTestStatus.Passed
                    ? actionEvidence.VerificationStatus == EvidenceVerificationStatus.Verified
                    : actionEvidence.Tests.Status == EvidenceTestStatus.NotRun
                      && actionEvidence.VerificationStatus == EvidenceVerificationStatus.Unverified);
Console.WriteLine(JsonSerializer.Serialize(
    new
    {
        RuntimeRoot = runRoot,
        Passed = passed,
        Results = results
    },
    new JsonSerializerOptions { WriteIndented = true }));
return passed ? 0 : 1;

static async Task PrepareProjectAsync(string projectRoot)
{
    await File.WriteAllTextAsync(
        Path.Combine(projectRoot, "EvidenceSample.csproj"),
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <IsTestProject>true</IsTestProject>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
            <PackageReference Include="xunit" Version="2.9.3" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
          </ItemGroup>
        </Project>
        """);
    await File.WriteAllTextAsync(
        Path.Combine(projectRoot, "Calculator.cs"),
        "namespace EvidenceSample; public static class Calculator { public static int Add(int a, int b) => a - b; }\n");
    await File.WriteAllTextAsync(
        Path.Combine(projectRoot, "CalculatorTests.cs"),
        "using Xunit; namespace EvidenceSample; public sealed class CalculatorTests { [Fact] public void Adds() => Assert.Equal(5, Calculator.Add(2, 3)); }\n");
    await File.WriteAllTextAsync(Path.Combine(projectRoot, ".gitignore"), "bin/\nobj/\n");
    await RunAsync(projectRoot, "git", "init", "--quiet");
    await RunAsync(projectRoot, "git", "add", ".");
    await RunAsync(
        projectRoot,
        "git",
        "-c",
        "user.name=ScreenGuide Test",
        "-c",
        "user.email=test@example.invalid",
        "commit",
        "--quiet",
        "-m",
        "baseline");
    var preflight = await RunAsync(
        projectRoot,
        "dotnet",
        "test",
        "EvidenceSample.csproj",
        "--nologo");
    if (preflight == 0)
    {
        throw new InvalidOperationException("证据测试项目的初始缺陷没有被测试发现。 ");
    }
}

static async Task<int> RunAsync(string workingDirectory, string executable, params string[] arguments)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }
    };
    foreach (var argument in arguments)
    {
        process.StartInfo.ArgumentList.Add(argument);
    }

    if (!process.Start())
    {
        throw new InvalidOperationException($"无法启动 {executable}。 ");
    }

    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    _ = await output;
    var standardError = await error;
    if (executable == "git" && process.ExitCode != 0)
    {
        throw new InvalidOperationException($"Git 初始化失败：{standardError}");
    }

    return process.ExitCode;
}

static async Task<AgentTask> CreateTaskAsync(
    ILocalTaskStore store,
    DeviceRecord device,
    ProjectRecord project,
    string title,
    string instruction)
{
    var now = DateTimeOffset.UtcNow;
    var command = new CommandRecord
    {
        Id = Guid.NewGuid(),
        SourceDeviceId = device.Id,
        ProjectId = project.Id,
        IdempotencyKey = $"2c-{Guid.NewGuid():N}",
        CommandType = CommandType.CreateTask,
        PayloadJson = JsonSerializer.Serialize(new { instruction }),
        ReceivedAtUtc = now,
        ExpiresAtUtc = now.AddMinutes(10),
        Status = CommandStatus.Received
    };
    await store.RegisterCommandAsync(command);
    var task = new AgentTask
    {
        Id = Guid.NewGuid(),
        ProjectId = project.Id,
        CreatedByDeviceId = device.Id,
        Title = title,
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

static TaskEvidence RequireEvidence(TaskEvidence? evidence) =>
    evidence ?? throw new InvalidOperationException("任务终态后没有生成 TaskEvidence。 ");

static object ToResult(string scenario, TaskEvidence evidence) => new
{
    Scenario = scenario,
    TaskStatus = evidence.TaskStatus.ToString(),
    Verification = evidence.VerificationStatus.ToString(),
    Summary = evidence.UserSummary,
    ChangedFiles = evidence.Git.ChangedFiles.Select(item => item.RelativePath).ToArray(),
    PreExistingFiles = evidence.Git.PreExistingChangedFiles,
    TestStatus = evidence.Tests.Status.ToString(),
    TestCommands = evidence.Tests.Commands.Select(item => new
    {
        item.Command,
        item.ExitCode,
        item.TotalTests,
        item.PassedTests,
        item.FailedTests
    }).ToArray(),
    CodexVersion = evidence.Connector.DetectedVersion,
    VersionVerified = evidence.Connector.VersionVerified
};
