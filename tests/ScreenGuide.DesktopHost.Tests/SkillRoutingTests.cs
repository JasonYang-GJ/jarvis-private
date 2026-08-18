using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class SkillRoutingTests
{
    [Fact]
    public void RoutesCodexTaskToOnlySupportedSkill()
    {
        var adapter = new FakeSkillAdapter("codex.project-task");
        var registry = new SkillAdapterRegistry([adapter]);
        registry.Initialize();
        var router = new TaskSkillRouter(registry);
        var task = Task("codex");

        var route = router.Route(task);

        Assert.Same(adapter, route.Adapter);
        Assert.Equal("coding.execute", route.Capability);
        Assert.Equal(["codex.project-task"], registry.SkillIds);
    }

    [Fact]
    public void RejectsUnknownExecutorAndDuplicateSkillIds()
    {
        var registry = new SkillAdapterRegistry([new FakeSkillAdapter("codex.project-task")]);
        registry.Initialize();
        var router = new TaskSkillRouter(registry);

        Assert.Throws<NotSupportedException>(() => router.Route(Task("unknown")));
        Assert.Throws<InvalidOperationException>(() =>
            new SkillAdapterRegistry(
            [
                new FakeSkillAdapter("same"),
                new FakeSkillAdapter("same")
            ]).Initialize());
    }

    private static AgentTask Task(string executor) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Guid.NewGuid(),
        CreatedByDeviceId = Guid.NewGuid(),
        Title = "route",
        Instruction = "test",
        Executor = executor,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };

    private sealed class FakeSkillAdapter(string id) : ISkillAdapter
    {
        public SkillDescriptor Descriptor { get; } = new(
            id,
            "1.0.0",
            id,
            true,
            new HashSet<string> { "coding.execute" });

        public Task<SkillStartResult> StartAsync(
            SkillInvocationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SkillStatusSnapshot> GetStatusAsync(
            SkillRunReference run,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<SkillEvent> GetEventsAsync(
            SkillRunReference run,
            long afterSequence,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CancelAsync(
            SkillRunReference run,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SkillStartResult> RespondAsync(
            SkillDecisionResponse response,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SkillFinalResult?> GetFinalResultAsync(
            SkillRunReference run,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
