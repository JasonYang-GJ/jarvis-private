using System.Runtime.CompilerServices;
using ScreenGuide.Agent.Abstractions;
using ScreenGuide.DesktopHost.Runtime;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class AgentConnectorRegistryTests
{
    [Fact]
    public void AllowsEmptyConnectorContainerForStage2A()
    {
        var registry = new AgentConnectorRegistry(Array.Empty<IAgentConnector>());

        registry.Initialize();

        Assert.Empty(registry.ConnectorIds);
        Assert.False(registry.TryGet("codex", out _));
    }

    [Fact]
    public void ResolvesRegisteredConnectorByIdCaseInsensitively()
    {
        var connector = new FakeConnector("codex");
        var registry = new AgentConnectorRegistry([connector]);
        registry.Initialize();

        var resolved = registry.GetRequired("CODEX");

        Assert.Same(connector, resolved);
    }

    [Fact]
    public void RejectsDuplicateConnectorIds()
    {
        var registry = new AgentConnectorRegistry(
            [new FakeConnector("codex"), new FakeConnector("CODEX")]);

        Assert.Throws<InvalidOperationException>(registry.Initialize);
    }

    internal sealed class FakeConnector(string connectorId) : IAgentConnector
    {
        public string ConnectorId { get; } = connectorId;

        public Task<AgentStartResult> StartTaskAsync(
            AgentStartRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new AgentStartResult(
                    new AgentRunReference(request.TaskId, "fake-run"),
                    AgentExecutionStatus.Running,
                    DateTimeOffset.UtcNow));

        public Task<AgentStatusSnapshot> GetTaskStatusAsync(
            AgentRunReference run,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new AgentStatusSnapshot(
                    run,
                    AgentExecutionStatus.Running,
                    DateTimeOffset.UtcNow));

        public async IAsyncEnumerable<AgentConnectorEvent> GetTaskEventsAsync(
            AgentRunReference run,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }

        public Task CancelTaskAsync(
            AgentRunReference run,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AgentStartResult> RespondToDecisionAsync(
            AgentDecisionResponse response,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new AgentStartResult(
                    response.Run with { AttemptId = response.AttemptId },
                    AgentExecutionStatus.Running,
                    DateTimeOffset.UtcNow));

        public Task<AgentFinalResult?> GetFinalResultAsync(
            AgentRunReference run,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentFinalResult?>(null);
    }
}
