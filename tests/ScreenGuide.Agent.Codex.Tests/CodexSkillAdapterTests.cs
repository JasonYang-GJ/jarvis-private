using System.Text.Json;
using ScreenGuide.Agent.Codex;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Agent.Codex.Tests;

public sealed class CodexSkillAdapterTests
{
    [Fact]
    public async Task RunsCodexThroughGenericSkillLifecycle()
    {
        await using var environment = ConnectorTestEnvironment.Create();
        await using var connector = environment.CreateConnector();
        var adapter = new CodexSkillAdapter(connector);
        var request = NewRequest(environment, "TEST_SUCCESS");

        var start = await adapter.StartAsync(request);
        var events = await CollectAsync(adapter, start.Run);
        var status = await adapter.GetStatusAsync(start.Run);
        var final = await adapter.GetFinalResultAsync(start.Run);

        Assert.Equal("codex.project-task", adapter.Descriptor.Id);
        Assert.Equal(SkillExecutionStatus.Succeeded, status.Status);
        Assert.Contains(events, item => item.EventKind == SkillEventKind.Completed);
        Assert.True(final?.Succeeded);
        Assert.NotNull(start.Run.ExternalRunId);
    }

    [Fact]
    public async Task RejectsInputWhoseProjectDoesNotMatchAuthorizedScope()
    {
        await using var environment = ConnectorTestEnvironment.Create();
        await using var connector = environment.CreateConnector();
        var adapter = new CodexSkillAdapter(connector);
        var request = NewRequest(environment, "TEST_SUCCESS") with
        {
            ResourceScopes =
            [
                new SkillResourceScope(
                    "Project",
                    Guid.NewGuid(),
                    Path.Combine(environment.RootDirectory, "different"),
                    "Execute")
            ]
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.StartAsync(request));
    }

    [Fact]
    public async Task ContinuesWaitingCodexSkillOnTheSameThread()
    {
        await using var environment = ConnectorTestEnvironment.Create();
        await using var connector = environment.CreateConnector();
        var adapter = new CodexSkillAdapter(connector);
        var first = await adapter.StartAsync(NewRequest(environment, "TEST_ACTION_REQUIRED"));
        _ = await CollectAsync(adapter, first.Run);
        var waiting = await adapter.GetStatusAsync(first.Run);

        var second = await adapter.RespondAsync(new SkillDecisionResponse(
            first.Run,
            waiting.DecisionRequestId!,
            "TEST_CONTINUE",
            Guid.NewGuid()));
        var events = await CollectAsync(adapter, second.Run);

        Assert.Equal(first.Run.ExternalRunId, second.Run.ExternalRunId);
        Assert.Contains(events, item => item.EventKind == SkillEventKind.Completed);
    }

    private static SkillInvocationRequest NewRequest(
        ConnectorTestEnvironment environment,
        string instruction)
    {
        var input = JsonSerializer.Serialize(new CodexSkillInput(
            environment.ProjectRoot,
            environment.ProjectRoot,
            instruction));
        return new SkillInvocationRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "codex.project-task",
            "coding.execute",
            input,
            [new SkillResourceScope("Project", Guid.NewGuid(), environment.ProjectRoot, "Execute")],
            SkillAuthorizationOrigin.ExplicitUser,
            Guid.NewGuid(),
            Guid.NewGuid());
    }

    private static async Task<IReadOnlyList<SkillEvent>> CollectAsync(
        ISkillAdapter adapter,
        SkillRunReference run)
    {
        var events = new List<SkillEvent>();
        await foreach (var item in adapter.GetEventsAsync(run, 0))
        {
            events.Add(item);
        }

        return events;
    }
}
