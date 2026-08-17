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
}
