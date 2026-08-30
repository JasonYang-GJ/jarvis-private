using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopHost.Runtime;
using AgentTaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class TaskStateChangeHubTests
{
    [Fact]
    public async Task SynchronizerSubscribesBeforeReadAndIgnoresDuplicateOlderAndUnrelatedEvents()
    {
        var hub = new TaskStateChangeHub();
        var taskId = Guid.NewGuid();
        var reader = new MutableTaskStateReader(State(taskId, 1, 1, AgentTaskStatus.Running));
        var synchronizer = new SessionTaskStateSynchronizer(hub, reader);
        var updates = new List<SessionTaskStateUpdate>();
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running = synchronizer.RunAsync(
            Guid.NewGuid(),
            taskId,
            (update, _) =>
            {
                lock (updates)
                {
                    updates.Add(update);
                }

                if (update.Finalized)
                {
                    terminal.TrySetResult();
                }

                return Task.CompletedTask;
            },
            cancellation.Token);

        await reader.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
        reader.Current = State(taskId, 2, 2, AgentTaskStatus.WaitingForUser);
        hub.Publish(reader.Current);
        hub.Publish(State(taskId, 1, 1, AgentTaskStatus.Running));
        hub.Publish(State(Guid.NewGuid(), 20, 20, AgentTaskStatus.Succeeded, finalized: true));
        await WaitForAsync(() => updates.Any(item => item.Phase == "WaitingForUser"));

        reader.Current = State(taskId, 3, 3, AgentTaskStatus.Succeeded, finalized: true);
        hub.Publish(reader.Current);
        await terminal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(hub.Publish(State(taskId, 4, 4, AgentTaskStatus.Running)));
        await running.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["ProgrammingTask", "WaitingForUser", "Completed"], updates.Select(item => item.Phase));
        Assert.True(updates[^1].Finalized);
    }

    [Fact]
    public async Task AuthoritativeReadFailureInterruptsOnlyTheSessionProjection()
    {
        var hub = new TaskStateChangeHub();
        var taskId = Guid.NewGuid();
        var reader = new ThrowingTaskStateReader();
        var synchronizer = new SessionTaskStateSynchronizer(hub, reader);
        SessionTaskStateUpdate? observed = null;

        await synchronizer.RunAsync(
            Guid.NewGuid(),
            taskId,
            (update, _) =>
            {
                observed = update;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Equal("Interrupted", observed.Phase);
        Assert.Equal("task_state_sync_failed", observed.FailureCode);
        Assert.Equal(1, reader.ReadCount);
    }

    private static LocalTaskStateSnapshot State(
        Guid taskId,
        long version,
        long eventSequence,
        AgentTaskStatus status,
        bool finalized = false) => new(
        taskId,
        version,
        eventSequence,
        finalized,
        status,
        status == AgentTaskStatus.WaitingForUser ? "等待补充" : "结果",
        status == AgentTaskStatus.Failed ? "task_failed" : null,
        status == AgentTaskStatus.Failed ? "任务失败" : null,
        finalized ? DateTimeOffset.UtcNow : null);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class MutableTaskStateReader(LocalTaskStateSnapshot current) : ILocalTaskStateReader
    {
        public LocalTaskStateSnapshot Current { get; set; } = current;

        public TaskCompletionSource FirstRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<LocalTaskStateSnapshot?> ReadTaskStateAsync(
            Guid taskId,
            CancellationToken cancellationToken = default)
        {
            FirstRead.TrySetResult();
            return Task.FromResult<LocalTaskStateSnapshot?>(Current);
        }
    }

    private sealed class ThrowingTaskStateReader : ILocalTaskStateReader
    {
        public int ReadCount { get; private set; }

        public Task<LocalTaskStateSnapshot?> ReadTaskStateAsync(
            Guid taskId,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            throw new IOException("synthetic authoritative read failure");
        }
    }
}
