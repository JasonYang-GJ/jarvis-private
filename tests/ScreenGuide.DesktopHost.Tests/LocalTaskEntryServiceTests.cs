using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopHost.Runtime;
using AgentTaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class LocalTaskEntryServiceTests
{
    [Fact]
    public async Task LocalEntryCompletesNormalTaskWithVerifiedEvidence()
    {
        var result = await RunTerminalTaskAsync("TEST_EVIDENCE_PASS");

        Assert.Equal(AgentTaskStatus.Succeeded, result.Task.Status);
        Assert.Equal(EvidenceVerificationStatus.Verified, result.Evidence?.VerificationStatus);
        Assert.Contains("执行 12 项测试，全部通过", result.Evidence?.UserSummary);
        Assert.NotEmpty(result.Events);
    }

    [Fact]
    public async Task LocalEntryShowsTestFailureAheadOfAgentSuccessClaim()
    {
        var result = await RunTerminalTaskAsync("TEST_EVIDENCE_FAIL");

        Assert.Equal(AgentTaskStatus.Succeeded, result.Task.Status);
        Assert.Equal(EvidenceVerificationStatus.VerificationFailed, result.Evidence?.VerificationStatus);
        Assert.True(result.Evidence?.AgentClaimContradictedByEvidence);
        Assert.Equal(EvidenceTestStatus.Failed, result.Evidence?.Tests.Status);
        Assert.Contains("任务不能标记为已验证完成", result.Evidence?.UserSummary);
    }

    [Fact]
    public async Task LocalEntryExplicitlyShowsMissingTestEvidence()
    {
        var result = await RunTerminalTaskAsync("TEST_EVIDENCE_NO_TEST");

        Assert.Equal(AgentTaskStatus.Succeeded, result.Task.Status);
        Assert.Equal(EvidenceVerificationStatus.Unverified, result.Evidence?.VerificationStatus);
        Assert.Equal(EvidenceTestStatus.NotRun, result.Evidence?.Tests.Status);
        Assert.Contains("测试结果尚未验证", result.Evidence?.UserSummary);
    }

    [Fact]
    public async Task LocalEntryCancellationStopsChildAndShowsCancelledEvidence()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, project, _) = await environment.SeedProjectsAsync();
        using var processes = new ReadyProcessTree(environment.RootDirectory);
        using var host = environment.BuildHost();
        await host.StartAsync();
        var entry = host.Services.GetRequiredService<LocalTaskEntryService>();
        var execution = host.Services.GetRequiredService<AgentTaskExecutionService>();

        var created = await entry.CreateTaskAsync(new CreateLocalTaskRequest(
            project.Id,
            processes.Prompt,
            "取消验收"));
        await processes.WaitUntilReadyAsync();
        var cancelled = await entry.CancelTaskAsync(created.TaskId, "local-entry-cancel");
        await execution.WaitForTaskAsync(created.TaskId);
        await processes.AssertStoppedAsync();
        var details = await entry.GetTaskDetailsAsync(created.TaskId);
        await host.StopAsync();

        Assert.False(cancelled.WasDuplicate);
        Assert.Equal(AgentTaskStatus.Cancelled, details?.Task.Status);
        Assert.Equal(EvidenceVerificationStatus.Cancelled, details?.Evidence?.VerificationStatus);
    }

    [Fact]
    public async Task LocalEntryContinuesWaitingTaskOnSameThread()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, project, _) = await environment.SeedProjectsAsync();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var entry = host.Services.GetRequiredService<LocalTaskEntryService>();
        var execution = host.Services.GetRequiredService<AgentTaskExecutionService>();

        var created = await entry.CreateTaskAsync(new CreateLocalTaskRequest(
            project.Id,
            "TEST_ACTION_REQUIRED",
            "等待用户验收"));
        await execution.WaitForTaskAsync(created.TaskId);
        var waiting = await entry.GetTaskDetailsAsync(created.TaskId);
        Assert.Equal(AgentTaskStatus.WaitingForUser, waiting?.Task.Status);
        Assert.NotNull(waiting?.PendingDecision);
        var threadId = waiting?.AgentRun?.ExternalRunId;

        var continued = await entry.ContinueTaskAsync(
            created.TaskId,
            "TEST_CONTINUE",
            "local-entry-continue");
        await execution.WaitForTaskAsync(created.TaskId);
        var completed = await entry.GetTaskDetailsAsync(created.TaskId);
        await host.StopAsync();

        Assert.False(continued.WasDuplicate);
        Assert.Equal(AgentTaskStatus.Succeeded, completed?.Task.Status);
        Assert.Equal(threadId, completed?.AgentRun?.ExternalRunId);
        Assert.Null(completed?.PendingDecision);
        Assert.NotNull(completed?.Evidence);
    }

    [Fact]
    public async Task LocalEntryShowsClaimAndGitEvidenceConflict()
    {
        var result = await RunTerminalTaskAsync("TEST_EVIDENCE_FALSE_FILE_CLAIM");

        Assert.Equal(AgentTaskStatus.Succeeded, result.Task.Status);
        Assert.Equal(EvidenceVerificationStatus.VerificationFailed, result.Evidence?.VerificationStatus);
        Assert.True(result.Evidence?.AgentClaimContradictedByEvidence);
        Assert.Empty(result.Evidence?.Git.ChangedFiles ?? []);
        Assert.Contains("实际证据不一致", result.Evidence?.UserSummary);
    }

    [Fact]
    public async Task LocalEntryReadsHistoricalTaskAndEvidenceAfterHostRestart()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, project, _) = await environment.SeedProjectsAsync();
        Guid taskId;
        using (var firstHost = environment.BuildHost())
        {
            await firstHost.StartAsync();
            var entry = firstHost.Services.GetRequiredService<LocalTaskEntryService>();
            var execution = firstHost.Services.GetRequiredService<AgentTaskExecutionService>();
            taskId = (await entry.CreateTaskAsync(new CreateLocalTaskRequest(
                project.Id,
                "TEST_EVIDENCE_PASS",
                "重启历史验收"))).TaskId;
            await execution.WaitForTaskAsync(taskId);
            await firstHost.StopAsync();
        }

        using (var secondHost = environment.BuildHost())
        {
            await secondHost.StartAsync();
            var entry = secondHost.Services.GetRequiredService<LocalTaskEntryService>();
            var projects = await entry.GetAuthorizedProjectsAsync();
            var tasks = await entry.GetTasksAsync(project.Id);
            var details = await entry.GetTaskDetailsAsync(taskId);
            await secondHost.StopAsync();

            Assert.Contains(projects, item => item.Id == project.Id);
            Assert.Contains(tasks, item => item.Id == taskId);
            Assert.Equal(AgentTaskStatus.Succeeded, details?.Task.Status);
            Assert.Equal(EvidenceVerificationStatus.Verified, details?.Evidence?.VerificationStatus);
            Assert.NotEmpty(details?.Events ?? []);
        }
    }

    [Fact]
    public async Task RepeatedCreateCommandDoesNotStartSecondTask()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, project, _) = await environment.SeedProjectsAsync();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var entry = host.Services.GetRequiredService<LocalTaskEntryService>();
        const string idempotencyKey = "local-entry-create-duplicate";

        var first = await entry.CreateTaskAsync(new CreateLocalTaskRequest(
            project.Id,
            "TEST_EVIDENCE_NO_TEST",
            IdempotencyKey: idempotencyKey));
        var second = await entry.CreateTaskAsync(new CreateLocalTaskRequest(
            project.Id,
            "TEST_EVIDENCE_NO_TEST",
            IdempotencyKey: idempotencyKey));
        await host.Services.GetRequiredService<AgentTaskExecutionService>().WaitForTaskAsync(first.TaskId);
        var tasks = await entry.GetTasksAsync(project.Id);
        await host.StopAsync();

        Assert.False(first.WasDuplicate);
        Assert.True(second.WasDuplicate);
        Assert.Equal(first.TaskId, second.TaskId);
        Assert.Single(tasks);
    }

    private static async Task<LocalTaskDetails> RunTerminalTaskAsync(string instruction)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, project, _) = await environment.SeedProjectsAsync();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var entry = host.Services.GetRequiredService<LocalTaskEntryService>();
        var execution = host.Services.GetRequiredService<AgentTaskExecutionService>();

        var created = await entry.CreateTaskAsync(new CreateLocalTaskRequest(
            project.Id,
            instruction,
            $"本地入口 {instruction}"));
        await execution.WaitForTaskAsync(created.TaskId);
        var details = await entry.GetTaskDetailsAsync(created.TaskId);
        await host.StopAsync();
        return Assert.IsType<LocalTaskDetails>(details);
    }
}
