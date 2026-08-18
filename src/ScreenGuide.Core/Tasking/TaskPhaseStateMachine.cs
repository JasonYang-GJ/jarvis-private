namespace ScreenGuide.Core.Tasking;

public static class TaskPhaseStateMachine
{
    private static readonly IReadOnlyDictionary<TaskPhase, IReadOnlySet<TaskPhase>> AllowedTransitions =
        new Dictionary<TaskPhase, IReadOnlySet<TaskPhase>>
        {
            [TaskPhase.Planning] = Set(TaskPhase.Routing),
            [TaskPhase.Routing] = Set(TaskPhase.AwaitingPermission, TaskPhase.Executing),
            [TaskPhase.AwaitingPermission] = Set(TaskPhase.Routing, TaskPhase.Executing),
            [TaskPhase.Executing] = Set(TaskPhase.AwaitingPermission, TaskPhase.Verifying),
            [TaskPhase.Verifying] = Set(TaskPhase.Routing)
        };

    public static bool CanTransition(TaskPhase from, TaskPhase to) =>
        from != to
        && AllowedTransitions.TryGetValue(from, out var allowed)
        && allowed.Contains(to);

    public static void EnsureTransition(TaskPhase from, TaskPhase to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"不允许任务阶段从 {from} 变更为 {to}。");
        }
    }

    private static IReadOnlySet<TaskPhase> Set(params TaskPhase[] phases) =>
        new HashSet<TaskPhase>(phases);
}
