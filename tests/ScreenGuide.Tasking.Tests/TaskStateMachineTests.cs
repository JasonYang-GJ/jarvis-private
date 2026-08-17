using AgentTaskStatus = ScreenGuide.Core.Tasking.TaskStatus;
using ScreenGuide.Core.Tasking;

namespace ScreenGuide.Tasking.Tests;

public sealed class TaskStateMachineTests
{
    [Theory]
    [InlineData(AgentTaskStatus.Pending, AgentTaskStatus.Running)]
    [InlineData(AgentTaskStatus.Running, AgentTaskStatus.WaitingForUser)]
    [InlineData(AgentTaskStatus.WaitingForUser, AgentTaskStatus.Running)]
    [InlineData(AgentTaskStatus.Running, AgentTaskStatus.Succeeded)]
    [InlineData(AgentTaskStatus.Running, AgentTaskStatus.Interrupted)]
    [InlineData(AgentTaskStatus.CancellationRequested, AgentTaskStatus.Cancelled)]
    [InlineData(AgentTaskStatus.Interrupted, AgentTaskStatus.Pending)]
    public void AllowsDocumentedTransitions(AgentTaskStatus from, AgentTaskStatus to)
    {
        Assert.True(TaskStateMachine.CanTransition(from, to));
        TaskStateMachine.EnsureTransition(from, to);
    }

    [Theory]
    [InlineData(AgentTaskStatus.Pending, AgentTaskStatus.Succeeded)]
    [InlineData(AgentTaskStatus.Succeeded, AgentTaskStatus.Running)]
    [InlineData(AgentTaskStatus.Failed, AgentTaskStatus.Pending)]
    [InlineData(AgentTaskStatus.Cancelled, AgentTaskStatus.Running)]
    [InlineData(AgentTaskStatus.Running, AgentTaskStatus.Running)]
    public void RejectsUndocumentedTransitions(AgentTaskStatus from, AgentTaskStatus to)
    {
        Assert.False(TaskStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidOperationException>(() => TaskStateMachine.EnsureTransition(from, to));
    }
}
