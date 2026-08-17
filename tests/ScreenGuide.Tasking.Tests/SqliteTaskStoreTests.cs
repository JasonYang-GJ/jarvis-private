using ScreenGuide.Core.Tasking;
using ScreenGuide.Persistence.Runtime;
using ScreenGuide.Persistence.Sqlite;
using AgentTaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

namespace ScreenGuide.Tasking.Tests;

public sealed class SqliteTaskStoreTests
{
    [Fact]
    public async Task PersistsTaskCommandEventAndAuditAcrossStoreInstances()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var (task, command) = await environment.CreateTaskAsync();

        await using var reopened = new SqliteTaskStore(environment.DatabasePath);
        await reopened.InitializeAsync();
        var persistedTask = await reopened.GetTaskAsync(task.Id);
        var persistedCommand = await reopened.GetCommandAsync(command.Id);
        var events = await reopened.GetTaskEventsAsync(task.Id);
        var audit = await reopened.GetAuditLogAsync();

        Assert.NotNull(persistedTask);
        Assert.Equal(AgentTaskStatus.Pending, persistedTask.Status);
        Assert.NotNull(persistedCommand);
        Assert.Equal(CommandStatus.Processed, persistedCommand.Status);
        Assert.Equal(task.Id, persistedCommand.TaskId);
        Assert.Single(events);
        Assert.Equal(TaskEventType.Created, events[0].EventType);
        Assert.Contains(audit, entry => entry.Action == "TaskCreated" && entry.EntityId == task.Id.ToString("D"));
    }

    [Fact]
    public async Task AcceptsConcurrentDuplicateCommandOnlyOnce()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var registrations = await Task.WhenAll(
            Enumerable.Range(0, 12)
                .Select(_ => environment.Store.RegisterCommandAsync(
                    environment.NewCreateCommand("same-device-command"))));

        Assert.Single(registrations, result => result.Accepted);
        var persistedIds = registrations.Select(result => result.Command.Id).Distinct().ToArray();
        Assert.Single(persistedIds);
        var audit = await environment.Store.GetAuditLogAsync();
        Assert.Contains(audit, entry => entry.Action == "CommandDuplicateIgnored");
    }

    [Fact]
    public async Task IdempotencyKeyIsScopedToSourceDevice()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var secondDevice = environment.Device with
        {
            Id = Guid.NewGuid(),
            DisplayName = "Second test device"
        };
        await environment.Store.UpsertDeviceAsync(secondDevice);
        var first = environment.NewCreateCommand("shared-key");
        var second = environment.NewCreateCommand("shared-key") with
        {
            Id = Guid.NewGuid(),
            SourceDeviceId = secondDevice.Id
        };

        var firstResult = await environment.Store.RegisterCommandAsync(first);
        var secondResult = await environment.Store.RegisterCommandAsync(second);

        Assert.True(firstResult.Accepted);
        Assert.True(secondResult.Accepted);
        Assert.NotEqual(firstResult.Command.Id, secondResult.Command.Id);
    }

    [Fact]
    public async Task RejectsTaskWhenProjectAuthorizationWasRevoked()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var revokedAt = DateTimeOffset.UtcNow;
        await environment.Store.SetProjectAuthorizationAsync(environment.Project with
        {
            AuthorizationState = ProjectAuthorizationState.Revoked,
            RevokedAtUtc = revokedAt,
            UpdatedAtUtc = revokedAt
        });
        var command = environment.NewCreateCommand("revoked-project");
        await environment.Store.RegisterCommandAsync(command);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => environment.Store.CreateTaskAsync(environment.NewTask(), command.Id));
    }

    [Fact]
    public async Task RejectsTaskWorkingDirectoryOutsideAuthorizedProject()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var command = environment.NewCreateCommand("path-traversal");
        await environment.Store.RegisterCommandAsync(command);
        var task = environment.NewTask() with { WorkingDirectoryRelativePath = ".." };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => environment.Store.CreateTaskAsync(task, command.Id));
    }

    [Fact]
    public async Task CancellationIsPersistedAuditedAndSignalsRuntimeToken()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var (task, _) = await environment.CreateTaskAsync();
        await environment.Store.TransitionTaskAsync(
            task.Id,
            AgentTaskStatus.Running,
            TaskEventSource.System,
            "Executor started.");
        using var registry = new TaskCancellationRegistry();
        var token = registry.GetOrCreateToken(task.Id);
        var service = new TaskCancellationService(environment.Store, registry);

        var accepted = await service.RequestAsync(task.Id, environment.Device.Id);
        var persisted = await environment.Store.GetTaskAsync(task.Id);
        var events = await environment.Store.GetTaskEventsAsync(task.Id);
        var audit = await environment.Store.GetAuditLogAsync();

        Assert.True(accepted);
        Assert.True(token.IsCancellationRequested);
        Assert.NotNull(persisted);
        Assert.Equal(AgentTaskStatus.CancellationRequested, persisted.Status);
        Assert.NotNull(persisted.CancellationRequestedAtUtc);
        Assert.Equal(TaskEventType.CancellationRequested, events[^1].EventType);
        Assert.Contains(audit, entry =>
            entry.Action == "TaskCancellationRequested" && entry.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task RecoveryMarksUnfinishedTasksInterruptedWithoutRestartingThem()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var (task, _) = await environment.CreateTaskAsync();
        await environment.Store.TransitionTaskAsync(
            task.Id,
            AgentTaskStatus.Running,
            TaskEventSource.System,
            "Executor started.");
        var recoveredAt = DateTimeOffset.UtcNow.AddMinutes(1);
        var service = new TaskRecoveryService(environment.Store);

        var result = await service.RecoverAsync(recoveredAt);
        var secondRecovery = await service.RecoverAsync(recoveredAt.AddMinutes(1));
        var persisted = await environment.Store.GetTaskAsync(task.Id);
        var events = await environment.Store.GetTaskEventsAsync(task.Id);

        Assert.Contains(task.Id, result.InterruptedTaskIds);
        Assert.Empty(secondRecovery.InterruptedTaskIds);
        Assert.NotNull(persisted);
        Assert.Equal(AgentTaskStatus.Interrupted, persisted.Status);
        Assert.Equal(recoveredAt, persisted.UpdatedAtUtc);
        Assert.Equal(TaskEventType.RecoveryDetected, events[^1].EventType);
        Assert.Equal(TaskEventSource.Recovery, events[^1].Source);
    }

    [Fact]
    public async Task TerminalTaskCannotBeChangedOrCancelled()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var (task, _) = await environment.CreateTaskAsync();
        await environment.Store.TransitionTaskAsync(
            task.Id,
            AgentTaskStatus.Running,
            TaskEventSource.System,
            "Executor started.");
        await environment.Store.TransitionTaskAsync(
            task.Id,
            AgentTaskStatus.Succeeded,
            TaskEventSource.Agent,
            "Executor reported success.");

        var cancellationAccepted = await environment.Store.RequestCancellationAsync(
            task.Id,
            environment.Device.Id);

        Assert.False(cancellationAccepted);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => environment.Store.TransitionTaskAsync(
                task.Id,
                AgentTaskStatus.Running,
                TaskEventSource.System,
                "Invalid restart."));
    }

    [Fact]
    public async Task AgentEventIsIdempotentAtSqliteBoundary()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var (task, _) = await environment.CreateTaskAsync();
        var now = DateTimeOffset.UtcNow;
        var (run, attempt) = NewAgentAttempt(task, now);
        await environment.Store.CreateAgentAttemptAsync(run, attempt);
        await environment.Store.RecordAgentStartedAsync(
            task.Id,
            run.Id,
            attempt.Id,
            "thread-test-id",
            "codex-cli 0.151.0",
            1234,
            now);
        var request = new AgentEventApplyRequest
        {
            TaskId = task.Id,
            AgentRunId = run.Id,
            AttemptId = attempt.Id,
            SequenceNumber = 2,
            ExternalEventId = "same-terminal-event",
            EventKind = "Completed",
            RunStatus = AgentRunStatus.Succeeded,
            AttemptStatus = AgentAttemptStatus.Completed,
            TaskStatus = AgentTaskStatus.Succeeded,
            OccurredAtUtc = now.AddSeconds(1),
            Message = "Authoritative turn.completed received.",
            FinalSummary = "done",
            FinalResultJson = "{\"outcome\":\"completed\"}",
            ExitCode = 0
        };

        var first = await environment.Store.ApplyAgentEventAsync(request);
        var duplicate = await environment.Store.ApplyAgentEventAsync(request);
        var events = await environment.Store.GetTaskEventsAsync(task.Id);

        Assert.True(first.Applied);
        Assert.False(duplicate.Applied);
        Assert.Single(events, item => item.ToStatus == AgentTaskStatus.Succeeded);
        Assert.Equal(AgentTaskStatus.Succeeded, duplicate.Task.Status);
    }

    [Fact]
    public async Task RecoveryInterruptsAgentRunAndAttemptButPreservesThreadId()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var (task, _) = await environment.CreateTaskAsync();
        var now = DateTimeOffset.UtcNow;
        var (run, attempt) = NewAgentAttempt(task, now);
        await environment.Store.CreateAgentAttemptAsync(run, attempt);
        await environment.Store.RecordAgentStartedAsync(
            task.Id,
            run.Id,
            attempt.Id,
            "thread-preserved-after-restart",
            "codex-cli 0.151.0",
            4321,
            now);

        var recovered = await environment.Store.RecoverInterruptedTasksAsync(now.AddMinutes(1));
        var persistedRun = await environment.Store.GetAgentRunByTaskAsync(task.Id);
        var persistedAttempt = Assert.Single(await environment.Store.GetAgentAttemptsAsync(task.Id));

        Assert.Contains(task.Id, recovered.InterruptedTaskIds);
        Assert.Equal("thread-preserved-after-restart", persistedRun?.ExternalRunId);
        Assert.Equal(AgentRunStatus.Interrupted, persistedRun?.Status);
        Assert.Equal(AgentAttemptStatus.Interrupted, persistedAttempt.Status);
        Assert.Equal(AgentTaskStatus.Interrupted, (await environment.Store.GetTaskAsync(task.Id))?.Status);
    }

    [Fact]
    public async Task TaskEvidencePersistsAcrossStoreInstancesAndIsAudited()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var (task, _) = await environment.CreateTaskAsync();
        var now = DateTimeOffset.UtcNow;
        var evidence = new TaskEvidence
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            GeneratedAtUtc = now,
            TaskStatus = AgentTaskStatus.Succeeded,
            AgentClaim = new AgentClaimEvidence
            {
                Status = AgentClaimStatus.Completed,
                FinalExplanation = "done",
                ClaimedChangedFiles = ["a.cs"],
                ClaimedTests = ["dotnet test:passed"]
            },
            Git = new GitTaskEvidence
            {
                IsGitRepository = true,
                BeforeCapturedAtUtc = now.AddMinutes(-1),
                AfterCapturedAtUtc = now,
                PreExistingChangedFiles = [],
                BeforeStatus = [],
                AfterStatus = [],
                ChangedFiles =
                [
                    new EvidenceFileChange
                    {
                        RelativePath = "a.cs",
                        ChangeType = EvidenceFileChangeType.Modified,
                        AddedLines = 2,
                        DeletedLines = 1
                    }
                ],
                ModifiedFileCount = 1,
                AddedLineCount = 2,
                DeletedLineCount = 1,
                DiffStatVerified = true
            },
            Tests = new TaskTestEvidence
            {
                Status = EvidenceTestStatus.Passed,
                Commands =
                [
                    new TestCommandEvidence
                    {
                        Command = "dotnet test",
                        ExternalItemId = "test-1",
                        ExitCode = 0,
                        Status = EvidenceTestStatus.Passed,
                        TotalTests = 12,
                        PassedTests = 12,
                        FailedTests = 0,
                        SkippedTests = 0
                    }
                ],
                TotalTests = 12,
                PassedTests = 12,
                FailedTests = 0,
                SkippedTests = 0,
                HasRealExecutionEvidence = true
            },
            Connector = new ConnectorCompatibilityEvidence
            {
                ConnectorId = "codex",
                DetectedVersion = "0.147.0",
                VersionVerified = true,
                VerifiedVersions = ["0.147.0"],
                Decision = "allowed"
            },
            VerificationStatus = EvidenceVerificationStatus.Verified,
            VerificationReasons = [],
            UserSummary = "Codex 已完成任务，修改 1 个文件。执行 12 项测试，全部通过。"
        };

        await environment.Store.UpsertTaskEvidenceAsync(evidence);
        await using var reopened = new SqliteTaskStore(environment.DatabasePath);
        await reopened.InitializeAsync();
        var persisted = await reopened.GetTaskEvidenceAsync(task.Id);
        var audit = await reopened.GetAuditLogAsync();

        Assert.NotNull(persisted);
        Assert.Equal(EvidenceVerificationStatus.Verified, persisted.VerificationStatus);
        Assert.Equal("0.147.0", persisted.Connector.DetectedVersion);
        Assert.Equal(12, persisted.Tests.TotalTests);
        Assert.Equal("a.cs", Assert.Single(persisted.Git.ChangedFiles).RelativePath);
        Assert.Equal(evidence.UserSummary, persisted.UserSummary);
        Assert.Contains(audit, item =>
            item.Action == "TaskEvidenceRecorded" && item.EntityId == task.Id.ToString("D"));
    }

    private static (AgentRunRecord Run, AgentAttemptRecord Attempt) NewAgentAttempt(
        AgentTask task,
        DateTimeOffset now)
    {
        var run = new AgentRunRecord
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            ConnectorId = "codex",
            Transport = "exec-json-v1",
            Status = AgentRunStatus.Starting,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var attempt = new AgentAttemptRecord
        {
            Id = Guid.NewGuid(),
            AgentRunId = run.Id,
            TaskId = task.Id,
            AttemptNumber = 1,
            Operation = AgentAttemptOperation.Start,
            Status = AgentAttemptStatus.Starting,
            StartedAtUtc = now,
            InputHash = "test-input-hash"
        };
        return (run, attempt);
    }
}
