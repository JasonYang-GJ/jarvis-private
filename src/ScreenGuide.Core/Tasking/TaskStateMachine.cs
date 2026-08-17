namespace ScreenGuide.Core.Tasking;

public static class TaskStateMachine
{
    private static readonly IReadOnlyDictionary<TaskStatus, IReadOnlySet<TaskStatus>> AllowedTransitions =
        new Dictionary<TaskStatus, IReadOnlySet<TaskStatus>>
        {
            [TaskStatus.Pending] = Set(
                TaskStatus.Running,
                TaskStatus.CancellationRequested,
                TaskStatus.Failed),
            [TaskStatus.Running] = Set(
                TaskStatus.WaitingForUser,
                TaskStatus.CancellationRequested,
                TaskStatus.Succeeded,
                TaskStatus.Failed,
                TaskStatus.Interrupted),
            [TaskStatus.WaitingForUser] = Set(
                TaskStatus.Running,
                TaskStatus.CancellationRequested,
                TaskStatus.Failed,
                TaskStatus.Interrupted),
            [TaskStatus.CancellationRequested] = Set(
                TaskStatus.Cancelled,
                TaskStatus.Failed,
                TaskStatus.Interrupted),
            [TaskStatus.Interrupted] = Set(
                TaskStatus.Pending,
                TaskStatus.Running,
                TaskStatus.CancellationRequested,
                TaskStatus.Failed,
                TaskStatus.Cancelled),
            [TaskStatus.Succeeded] = Set(),
            [TaskStatus.Failed] = Set(),
            [TaskStatus.Cancelled] = Set()
        };

    public static bool IsTerminal(TaskStatus status) =>
        status is TaskStatus.Succeeded or TaskStatus.Failed or TaskStatus.Cancelled;

    public static bool CanTransition(TaskStatus from, TaskStatus to) =>
        AllowedTransitions[from].Contains(to);

    public static void EnsureTransition(TaskStatus from, TaskStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"不允许任务状态从 {from} 转换为 {to}。");
        }
    }

    private static IReadOnlySet<TaskStatus> Set(params TaskStatus[] statuses) =>
        new HashSet<TaskStatus>(statuses);
}
