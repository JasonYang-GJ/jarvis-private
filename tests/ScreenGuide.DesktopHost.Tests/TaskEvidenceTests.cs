using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopHost.Runtime;
using AgentTaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class TaskEvidenceTests
{
    [Fact]
    public async Task FileChangeAndPassingTestsAreSystemVerified()
    {
        var result = await RunTerminalTaskAsync("TEST_EVIDENCE_PASS");

        Assert.Equal(EvidenceVerificationStatus.Verified, result.Evidence.VerificationStatus);
        Assert.Equal(EvidenceTestStatus.Passed, result.Evidence.Tests.Status);
        Assert.Equal(12, result.Evidence.Tests.TotalTests);
        Assert.Equal(1, result.Evidence.Git.AddedFileCount);
        Assert.Equal("verified-change.txt", Assert.Single(result.Evidence.Git.ChangedFiles).RelativePath);
        Assert.Contains("执行 12 项测试，全部通过", result.Evidence.UserSummary);
    }

    [Fact]
    public async Task ClaimedSuccessCannotOverrideActualTestFailure()
    {
        var result = await RunTerminalTaskAsync("TEST_EVIDENCE_FAIL");

        Assert.Equal(AgentTaskStatus.Succeeded, result.Task.Status);
        Assert.Equal(EvidenceVerificationStatus.VerificationFailed, result.Evidence.VerificationStatus);
        Assert.True(result.Evidence.AgentClaimContradictedByEvidence);
        Assert.Equal(EvidenceTestStatus.Failed, result.Evidence.Tests.Status);
        Assert.Equal(2, result.Evidence.Tests.FailedTests);
        Assert.Contains("任务不能标记为已验证完成", result.Evidence.UserSummary);
    }

    [Fact]
    public async Task ClaimedSuccessWithNoFileChangesUsesActualGitEvidence()
    {
        var result = await RunTerminalTaskAsync("TEST_EVIDENCE_NO_CHANGE");

        Assert.Empty(result.Evidence.Git.ChangedFiles);
        Assert.False(result.Evidence.AgentClaimContradictedByEvidence);
        Assert.Equal(EvidenceVerificationStatus.Unverified, result.Evidence.VerificationStatus);
        Assert.Contains("未检测到本次文件变化", result.Evidence.UserSummary);
    }

    [Fact]
    public async Task ClaimedFileChangeWithoutActualChangeIsRejected()
    {
        var result = await RunTerminalTaskAsync("TEST_EVIDENCE_FALSE_FILE_CLAIM");

        Assert.Empty(result.Evidence.Git.ChangedFiles);
        Assert.True(result.Evidence.AgentClaimContradictedByEvidence);
        Assert.Equal(EvidenceVerificationStatus.VerificationFailed, result.Evidence.VerificationStatus);
        Assert.Contains("实际证据不一致", result.Evidence.UserSummary);
    }

    [Fact]
    public async Task MissingTestExecutionIsExplicitlyUnverified()
    {
        var result = await RunTerminalTaskAsync("TEST_EVIDENCE_NO_TEST");

        Assert.Equal(EvidenceTestStatus.NotRun, result.Evidence.Tests.Status);
        Assert.False(result.Evidence.Tests.HasRealExecutionEvidence);
        Assert.Equal(EvidenceVerificationStatus.Unverified, result.Evidence.VerificationStatus);
        Assert.Contains("测试结果尚未验证", result.Evidence.UserSummary);
    }

    [Fact]
    public async Task PreExistingUserChangeIsNotCountedAsTaskChange()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, project, task) = await environment.SeedTaskAsync("TEST_EVIDENCE_NO_CHANGE");
        await File.WriteAllTextAsync(Path.Combine(project.RootPath, "user-only.txt"), "user data\n");
        using var host = environment.BuildHost();
        await host.StartAsync();
        var service = host.Services.GetRequiredService<AgentTaskExecutionService>();

        await service.StartTaskAsync(task.Id);
        await service.WaitForTaskAsync(task.Id);
        var evidence = await host.Services.GetRequiredService<ILocalTaskStore>()
            .GetTaskEvidenceAsync(task.Id);
        await host.StopAsync();

        Assert.NotNull(evidence);
        Assert.Contains("user-only.txt", evidence.Git.PreExistingChangedFiles);
        Assert.Empty(evidence.Git.ChangedFiles);
    }

    [Fact]
    public async Task MixedUserAndAgentChangesAreSeparatedFromBaseline()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, project, task) = await environment.SeedTaskAsync("TEST_EVIDENCE_MIXED");
        var path = Path.Combine(project.RootPath, "mixed.txt");
        await File.WriteAllTextAsync(path, "base line\n");
        await DesktopHostTestEnvironment.RunGitAsync(project.RootPath, "add", "mixed.txt");
        await DesktopHostTestEnvironment.RunGitAsync(
            project.RootPath,
            "-c",
            "user.name=ScreenGuide Test",
            "-c",
            "user.email=test@example.invalid",
            "commit",
            "--quiet",
            "-m",
            "baseline");
        await File.AppendAllTextAsync(path, "user line\n");
        using var host = environment.BuildHost();
        await host.StartAsync();
        var service = host.Services.GetRequiredService<AgentTaskExecutionService>();

        await service.StartTaskAsync(task.Id);
        await service.WaitForTaskAsync(task.Id);
        var evidence = await host.Services.GetRequiredService<ILocalTaskStore>()
            .GetTaskEvidenceAsync(task.Id);
        await host.StopAsync();

        var change = Assert.Single(evidence!.Git.ChangedFiles);
        Assert.Equal("mixed.txt", change.RelativePath);
        Assert.True(change.HadPreExistingChanges);
        Assert.True(change.MixedWithPreExistingChanges);
        Assert.Equal(1, change.AddedLines);
    }

    [Fact]
    public async Task AddedModifiedAndDeletedFileCountsComeFromBaselineDiff()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, project, task) = await environment.SeedTaskAsync("TEST_EVIDENCE_FILE_COUNTS");
        await File.WriteAllTextAsync(Path.Combine(project.RootPath, "modified.txt"), "base\n");
        await File.WriteAllTextAsync(Path.Combine(project.RootPath, "deleted.txt"), "delete me\n");
        await DesktopHostTestEnvironment.RunGitAsync(project.RootPath, "add", ".");
        await DesktopHostTestEnvironment.RunGitAsync(
            project.RootPath,
            "-c",
            "user.name=ScreenGuide Test",
            "-c",
            "user.email=test@example.invalid",
            "commit",
            "--quiet",
            "-m",
            "baseline");
        using var host = environment.BuildHost();
        await host.StartAsync();
        var service = host.Services.GetRequiredService<AgentTaskExecutionService>();

        await service.StartTaskAsync(task.Id);
        await service.WaitForTaskAsync(task.Id);
        var evidence = await host.Services.GetRequiredService<ILocalTaskStore>()
            .GetTaskEvidenceAsync(task.Id);
        await host.StopAsync();

        Assert.NotNull(evidence);
        Assert.Equal(1, evidence.Git.AddedFileCount);
        Assert.Equal(1, evidence.Git.ModifiedFileCount);
        Assert.Equal(1, evidence.Git.DeletedFileCount);
        Assert.Equal(3, evidence.Git.ChangedFiles.Count);
        Assert.True(evidence.Git.DiffStatVerified);
    }

    [Fact]
    public async Task FailedTaskProducesFailedEvidence()
    {
        var result = await RunTerminalTaskAsync("TEST_FAILURE");

        Assert.Equal(AgentTaskStatus.Failed, result.Task.Status);
        Assert.Equal(EvidenceVerificationStatus.Failed, result.Evidence.VerificationStatus);
        Assert.Contains("任务失败", result.Evidence.UserSummary);
    }

    [Fact]
    public async Task ActionRequiredCreatesEvidenceOnlyAfterFinalTurn()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, _, task) = await environment.SeedTaskAsync("TEST_ACTION_REQUIRED");
        using var host = environment.BuildHost();
        await host.StartAsync();
        var service = host.Services.GetRequiredService<AgentTaskExecutionService>();
        var store = host.Services.GetRequiredService<ILocalTaskStore>();

        var first = await service.StartTaskAsync(task.Id);
        await service.WaitForTaskAsync(task.Id);
        Assert.Null(await store.GetTaskEvidenceAsync(task.Id));

        var second = await service.ContinueTaskAsync(task.Id, "TEST_CONTINUE");
        await service.WaitForTaskAsync(task.Id);
        var evidence = await store.GetTaskEvidenceAsync(task.Id);
        await host.StopAsync();

        Assert.Equal(first.ExternalRunId, second.ExternalRunId);
        Assert.NotNull(evidence);
        Assert.Equal(AgentTaskStatus.Succeeded, evidence.TaskStatus);
    }

    [Fact]
    public async Task NonGitProjectIsNeverReportedAsGitVerified()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, project, task) = await environment.SeedTaskAsync("TEST_EVIDENCE_NO_CHANGE");
        var gitDirectory = Path.GetFullPath(Path.Combine(project.RootPath, ".git"));
        Assert.StartsWith(environment.RootDirectory, gitDirectory, StringComparison.OrdinalIgnoreCase);
        Directory.Delete(gitDirectory, recursive: true);
        using var host = environment.BuildHost();
        await host.StartAsync();
        var service = host.Services.GetRequiredService<AgentTaskExecutionService>();

        await service.StartTaskAsync(task.Id);
        await service.WaitForTaskAsync(task.Id);
        var evidence = await host.Services.GetRequiredService<ILocalTaskStore>()
            .GetTaskEvidenceAsync(task.Id);
        await host.StopAsync();

        Assert.NotNull(evidence);
        Assert.False(evidence.Git.IsGitRepository);
        Assert.Equal(EvidenceVerificationStatus.Unverified, evidence.VerificationStatus);
        Assert.Contains("不是 Git 仓库", evidence.UserSummary);
    }

    [Fact]
    public async Task CancelledTaskProducesCancelledEvidence()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var marker = Path.Combine(environment.RootDirectory, "evidence-cancel-marker.txt");
        var (device, _, task) = await environment.SeedTaskAsync(
            $"TEST_LONG_RUNNING\nMARKER={marker}");
        using var host = environment.BuildHost();
        await host.StartAsync();
        var service = host.Services.GetRequiredService<AgentTaskExecutionService>();

        await service.StartTaskAsync(task.Id);
        await Task.Delay(500);
        await service.CancelTaskAsync(task.Id, device.Id);
        var evidence = await host.Services.GetRequiredService<ILocalTaskStore>()
            .GetTaskEvidenceAsync(task.Id);
        await host.StopAsync();

        Assert.NotNull(evidence);
        Assert.Equal(EvidenceVerificationStatus.Cancelled, evidence.VerificationStatus);
        Assert.Equal(AgentTaskStatus.Cancelled, evidence.TaskStatus);
    }

    [Fact]
    public async Task VersionMismatchIsRejectedAndRecordedInEvidence()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, _, task) = await environment.SeedTaskAsync("TEST_SUCCESS");
        var options = new DesktopHostOptions(
            environment.Options.DataDirectory,
            environment.CopyFakeCliToUnverifiedVersionDirectory());
        using var host = DesktopHostFactory.Build(
            Array.Empty<string>(),
            options,
            services => services.AddSingleton<TimeProvider>(environment.TimeProvider));
        await host.StartAsync();
        var service = host.Services.GetRequiredService<AgentTaskExecutionService>();

        await service.StartTaskAsync(task.Id);
        await service.WaitForTaskAsync(task.Id);
        var store = host.Services.GetRequiredService<ILocalTaskStore>();
        var evidence = await store.GetTaskEvidenceAsync(task.Id);
        var persistedTask = await store.GetTaskAsync(task.Id);
        await host.StopAsync();

        Assert.Equal(AgentTaskStatus.Failed, persistedTask?.Status);
        Assert.NotNull(evidence);
        Assert.Equal("0.148.0", evidence.Connector.DetectedVersion);
        Assert.False(evidence.Connector.VersionVerified);
        Assert.Contains("拒绝", evidence.Connector.Decision);
    }

    private static async Task<(AgentTask Task, TaskEvidence Evidence)> RunTerminalTaskAsync(
        string instruction)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, _, task) = await environment.SeedTaskAsync(instruction);
        using var host = environment.BuildHost();
        await host.StartAsync();
        var service = host.Services.GetRequiredService<AgentTaskExecutionService>();

        await service.StartTaskAsync(task.Id);
        await service.WaitForTaskAsync(task.Id);
        var store = host.Services.GetRequiredService<ILocalTaskStore>();
        var persisted = await store.GetTaskAsync(task.Id);
        var evidence = await store.GetTaskEvidenceAsync(task.Id);
        await host.StopAsync();

        return (persisted!, evidence!);
    }
}
