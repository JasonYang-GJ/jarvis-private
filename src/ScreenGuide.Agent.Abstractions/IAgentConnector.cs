namespace ScreenGuide.Agent.Abstractions;

public interface IAgentConnector
{
    string ConnectorId { get; }

    Task<AgentStartResult> StartTaskAsync(
        AgentStartRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentStatusSnapshot> GetTaskStatusAsync(
        AgentRunReference run,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgentConnectorEvent> GetTaskEventsAsync(
        AgentRunReference run,
        long afterSequence,
        CancellationToken cancellationToken = default);

    Task CancelTaskAsync(
        AgentRunReference run,
        CancellationToken cancellationToken = default);

    Task<AgentStartResult> RespondToDecisionAsync(
        AgentDecisionResponse response,
        CancellationToken cancellationToken = default);

    Task<AgentFinalResult?> GetFinalResultAsync(
        AgentRunReference run,
        CancellationToken cancellationToken = default);
}
