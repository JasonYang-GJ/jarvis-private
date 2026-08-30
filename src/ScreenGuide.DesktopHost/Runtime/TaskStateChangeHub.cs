using ScreenGuide.Core.Tasking;
using AgentTaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed record LocalTaskStateSnapshot(
    Guid TaskId,
    long Version,
    long LastEventSequence,
    bool Finalized,
    AgentTaskStatus Status,
    string? UserSummary,
    string? FailureCode,
    string? FailureMessage,
    DateTimeOffset? CompletedAtUtc);

public interface ILocalTaskStateReader
{
    Task<LocalTaskStateSnapshot?> ReadTaskStateAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);
}

public sealed record TaskStateChangeNotice(long HubSequence, LocalTaskStateSnapshot State);

public sealed class TaskStateChangeHub
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TaskStateSlot> _slots = [];

    public long Subscribe(Guid taskId)
    {
        lock (_gate)
        {
            return GetOrAdd(taskId).Notice?.HubSequence ?? 0;
        }
    }

    public bool Publish(LocalTaskStateSnapshot state)
    {
        lock (_gate)
        {
            var slot = GetOrAdd(state.TaskId);
            var current = slot.Notice?.State;
            if (current is not null)
            {
                if (IsTerminal(current.Status) && current.Finalized)
                {
                    return false;
                }

                if (state.Version < current.Version
                    || (state.Version == current.Version
                        && state.LastEventSequence < current.LastEventSequence)
                    || (state.Version == current.Version
                        && state.LastEventSequence == current.LastEventSequence
                        && state.Finalized == current.Finalized))
                {
                    return false;
                }
            }

            var sequence = (slot.Notice?.HubSequence ?? 0) + 1;
            slot.Notice = new TaskStateChangeNotice(sequence, state);
            var completed = slot.Next;
            slot.Next = NewSource();
            completed.TrySetResult(slot.Notice);
            return true;
        }
    }

    public async Task<TaskStateChangeNotice> WaitForChangeAsync(
        Guid taskId,
        long afterHubSequence,
        CancellationToken cancellationToken = default)
    {
        Task<TaskStateChangeNotice> wait;
        lock (_gate)
        {
            var slot = GetOrAdd(taskId);
            if (slot.Notice is { } current && current.HubSequence > afterHubSequence)
            {
                return current;
            }

            wait = slot.Next.Task;
        }

        return await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private TaskStateSlot GetOrAdd(Guid taskId)
    {
        if (!_slots.TryGetValue(taskId, out var slot))
        {
            slot = new TaskStateSlot();
            _slots.Add(taskId, slot);
        }

        return slot;
    }

    private static TaskCompletionSource<TaskStateChangeNotice> NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool IsTerminal(AgentTaskStatus status) => status is
        AgentTaskStatus.Succeeded or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled
        or AgentTaskStatus.Interrupted;

    private sealed class TaskStateSlot
    {
        public TaskStateChangeNotice? Notice { get; set; }

        public TaskCompletionSource<TaskStateChangeNotice> Next { get; set; } = NewSource();
    }
}

public sealed record SessionTaskStateUpdate(
    Guid TaskId,
    long TaskVersion,
    long LastEventSequence,
    bool Finalized,
    string Phase,
    string? ResultSummary,
    string? FailureCode,
    string? FailureMessage,
    DateTimeOffset? CompletedAtUtc);

public sealed class SessionTaskStateSynchronizer(
    TaskStateChangeHub changes,
    ILocalTaskStateReader reader)
{
    public async Task RunAsync(
        Guid turnId,
        Guid taskId,
        Func<SessionTaskStateUpdate, CancellationToken, Task> applyAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applyAsync);
        var cursor = changes.Subscribe(taskId);
        long lastVersion = -1;
        long lastEventSequence = -1;
        try
        {
            while (true)
            {
                var state = await reader.ReadTaskStateAsync(taskId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidDataException("任务状态已经不存在。");
                if (state.TaskId != taskId)
                {
                    throw new InvalidDataException("任务状态与订阅目标不一致。");
                }

                var newer = state.Version > lastVersion
                            || (state.Version == lastVersion
                                && state.LastEventSequence > lastEventSequence)
                            || (state.Version == lastVersion
                                && state.LastEventSequence == lastEventSequence
                                && state.Finalized);
                var terminal = IsTerminal(state.Status);
                if (newer && (!terminal || state.Finalized))
                {
                    await applyAsync(Map(state), cancellationToken).ConfigureAwait(false);
                    lastVersion = state.Version;
                    lastEventSequence = state.LastEventSequence;
                    if (terminal)
                    {
                        return;
                    }
                }

                var notice = await changes.WaitForChangeAsync(taskId, cursor, cancellationToken)
                    .ConfigureAwait(false);
                cursor = notice.HubSequence;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            try
            {
                await applyAsync(
                        new SessionTaskStateUpdate(
                            taskId,
                            Math.Max(0, lastVersion),
                            Math.Max(0, lastEventSequence),
                            true,
                            "Interrupted",
                            null,
                            "task_state_sync_failed",
                            "编程任务状态暂时无法继续同步，请在任务页查看实际状态。",
                            DateTimeOffset.UtcNow),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Session 投影订阅失败不能反向阻塞或改写权威 Task。
            }
        }
    }

    private static SessionTaskStateUpdate Map(LocalTaskStateSnapshot state)
    {
        var phase = state.Status switch
        {
            AgentTaskStatus.Pending or AgentTaskStatus.Running or AgentTaskStatus.CancellationRequested =>
                "ProgrammingTask",
            AgentTaskStatus.WaitingForUser => "WaitingForUser",
            AgentTaskStatus.Succeeded => "Completed",
            AgentTaskStatus.Cancelled => "Cancelled",
            AgentTaskStatus.Interrupted => "Interrupted",
            _ => "Failed"
        };
        return new SessionTaskStateUpdate(
            state.TaskId,
            state.Version,
            state.LastEventSequence,
            state.Finalized,
            phase,
            state.UserSummary,
            phase == "Failed" ? state.FailureCode ?? "task_failed" : state.FailureCode,
            phase == "Failed" ? state.FailureMessage ?? "编程任务没有成功完成。" : state.FailureMessage,
            state.CompletedAtUtc);
    }

    private static bool IsTerminal(AgentTaskStatus status) => status is
        AgentTaskStatus.Succeeded or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled
        or AgentTaskStatus.Interrupted;
}
