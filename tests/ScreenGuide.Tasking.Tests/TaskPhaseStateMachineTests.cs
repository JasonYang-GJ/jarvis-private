using ScreenGuide.Core.Tasking;

namespace ScreenGuide.Tasking.Tests;

public sealed class TaskPhaseStateMachineTests
{
    [Theory]
    [InlineData(TaskPhase.Planning, TaskPhase.Routing)]
    [InlineData(TaskPhase.Routing, TaskPhase.AwaitingPermission)]
    [InlineData(TaskPhase.Routing, TaskPhase.Executing)]
    [InlineData(TaskPhase.AwaitingPermission, TaskPhase.Routing)]
    [InlineData(TaskPhase.AwaitingPermission, TaskPhase.Executing)]
    [InlineData(TaskPhase.Executing, TaskPhase.AwaitingPermission)]
    [InlineData(TaskPhase.Executing, TaskPhase.Verifying)]
    [InlineData(TaskPhase.Verifying, TaskPhase.Routing)]
    public void AllowsDeclaredTransitions(TaskPhase from, TaskPhase to) =>
        Assert.True(TaskPhaseStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(TaskPhase.Planning, TaskPhase.Executing)]
    [InlineData(TaskPhase.Routing, TaskPhase.Verifying)]
    [InlineData(TaskPhase.Verifying, TaskPhase.Executing)]
    [InlineData(TaskPhase.Executing, TaskPhase.Executing)]
    public void RejectsSkippedOrRepeatedTransitions(TaskPhase from, TaskPhase to)
    {
        Assert.False(TaskPhaseStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidOperationException>(() =>
            TaskPhaseStateMachine.EnsureTransition(from, to));
    }
}
