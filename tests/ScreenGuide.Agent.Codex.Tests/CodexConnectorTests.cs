using ScreenGuide.Agent.Abstractions;
using ScreenGuide.Agent.Codex;
using ScreenGuide.FakeCodexCli;

namespace ScreenGuide.Agent.Codex.Tests;

public sealed class CodexConnectorTests
{
    [Fact]
    public void VersionGateAcceptsOnlyVerifiedV01Version()
    {
        var verified = CodexVersionCompatibility.Evaluate("0.147.0");
        var unverified = CodexVersionCompatibility.Evaluate("0.148.0");

        Assert.True(verified.IsVerified);
        Assert.False(unverified.IsVerified);
        Assert.Equal(["0.147.0"], verified.VerifiedVersions);
        Assert.Contains("拒绝", unverified.Decision);
    }

    [Fact]
    public async Task UnverifiedCliVersionIsRejectedBeforeThreadStarts()
    {
        await using var environment = ConnectorTestEnvironment.Create();
        var executable = environment.CopyFakeCliToUnverifiedVersionDirectory();
        await using var connector = environment.CreateConnector(executable);

        var start = await connector.StartTaskAsync(
            environment.NewRequest(Guid.NewGuid(), "TEST_SUCCESS"));
        var events = await CollectAsync(connector, start.Run);

        Assert.Equal(AgentExecutionStatus.Failed, start.Status);
        Assert.Null(start.Run.ExternalRunId);
        Assert.Contains(events, item =>
            item.EventKind == AgentConnectorEventKind.Failed
            && item.DataJson?.Contains("0.148.0", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(events, item => item.EventKind == AgentConnectorEventKind.Completed);
    }
    [Fact]
    public async Task CompletesOnlyAfterAuthoritativeCompletedEvent()
    {
        await using var environment = ConnectorTestEnvironment.Create();
        await using var connector = environment.CreateConnector();

        var (start, events) = await StartAndCollectAsync(connector, environment, "TEST_SUCCESS");
        var status = await connector.GetTaskStatusAsync(start.Run);
        var final = await connector.GetFinalResultAsync(start.Run);

        Assert.Equal(AgentExecutionStatus.Succeeded, status.Status);
        Assert.Contains(events, item => item.EventKind == AgentConnectorEventKind.Completed);
        Assert.True(final?.Succeeded);
        Assert.Equal("completed", final?.Summary);
    }

    [Fact]
    public async Task MapsAuthoritativeTurnFailedToFailed()
    {
        await using var environment = ConnectorTestEnvironment.Create();
        await using var connector = environment.CreateConnector();

        var (start, events) = await StartAndCollectAsync(connector, environment, "TEST_FAILURE");

        Assert.Equal(
            AgentExecutionStatus.Failed,
            (await connector.GetTaskStatusAsync(start.Run)).Status);
        Assert.Single(events, item => item.EventKind == AgentConnectorEventKind.Failed);
        Assert.DoesNotContain(events, item => item.EventKind == AgentConnectorEventKind.Completed);
    }

    [Fact]
    public async Task ReportsStartupFailureWithoutCompleted()
    {
        await using var environment = ConnectorTestEnvironment.Create();
        await using var connector = environment.CreateConnector(
            Path.Combine(environment.RootDirectory, "missing-codex.exe"));
        var taskId = Guid.NewGuid();
        var start = await connector.StartTaskAsync(environment.NewRequest(taskId, "TEST_SUCCESS"));
        var events = await CollectAsync(connector, start.Run);

        Assert.Equal(AgentExecutionStatus.Failed, start.Status);
        Assert.Contains(events, item => item.EventKind == AgentConnectorEventKind.Failed);
        Assert.DoesNotContain(events, item => item.EventKind == AgentConnectorEventKind.Completed);
    }

    [Fact]
    public async Task ProcessExitWithoutTerminalEventIsInterruptedNotCompleted()
    {
        await using var environment = ConnectorTestEnvironment.Create();
        await using var connector = environment.CreateConnector();

        var (start, events) = await StartAndCollectAsync(connector, environment, "TEST_NO_TERMINAL");

        Assert.Equal(
            AgentExecutionStatus.Interrupted,
            (await connector.GetTaskStatusAsync(start.Run)).Status);
        Assert.Contains(events, item => item.EventKind == AgentConnectorEventKind.Interrupted);
        Assert.DoesNotContain(events, item => item.EventKind == AgentConnectorEventKind.Completed);
    }

    [Fact]
    public async Task DuplicateProtocolEventDoesNotDuplicateTerminalEvent()
    {
        await using var environment = ConnectorTestEnvironment.Create();
        await using var connector = environment.CreateConnector();

        var (_, events) = await StartAndCollectAsync(connector, environment, "TEST_DUPLICATE");

        Assert.Single(events, item => item.EventKind == AgentConnectorEventKind.Completed);
    }

    [Fact]
    public async Task ContinuesActionRequiredOnSameThread()
    {
        await using var environment = ConnectorTestEnvironment.Create();
        await using var connector = environment.CreateConnector();
        var taskId = Guid.NewGuid();
        var first = await connector.StartTaskAsync(
            environment.NewRequest(taskId, "TEST_ACTION_REQUIRED"));
        _ = await CollectAsync(connector, first.Run);
        var waiting = await connector.GetTaskStatusAsync(first.Run);
        var nextAttemptId = Guid.NewGuid();

        var second = await connector.RespondToDecisionAsync(
            new AgentDecisionResponse(
                first.Run,
                waiting.DecisionRequestId!,
                "TEST_CONTINUE",
                nextAttemptId,
                environment.ProjectRoot,
                environment.ProjectRoot));
        var events = await CollectAsync(connector, second.Run);

        Assert.Equal(first.Run.ExternalRunId, second.Run.ExternalRunId);
        Assert.Equal(nextAttemptId, second.Run.AttemptId);
        Assert.Contains(events, item => item.EventKind == AgentConnectorEventKind.Completed);
        Assert.Contains(
            first.Run.ExternalRunId!,
            (await connector.GetFinalResultAsync(second.Run))!.Summary,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationTerminatesJobObjectChildrenBeforeTheyModifyProject()
    {
        await using var environment = ConnectorTestEnvironment.Create();
        await using var connector = environment.CreateConnector();
        var marker = Path.Combine(environment.ProjectRoot, "should-not-exist.txt");
        var start = await connector.StartTaskAsync(
            environment.NewRequest(
                Guid.NewGuid(),
                $"TEST_LONG_RUNNING\nMARKER={marker}"));
        var collector = CollectAsync(connector, start.Run);
        await Task.Delay(500);

        await connector.CancelTaskAsync(start.Run);
        var events = await collector;
        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.Contains(events, item => item.EventKind == AgentConnectorEventKind.Cancelled);
        Assert.Equal(
            AgentExecutionStatus.Cancelled,
            (await connector.GetTaskStatusAsync(start.Run)).Status);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task RejectsWorkingDirectoryOutsideProject()
    {
        await using var environment = ConnectorTestEnvironment.Create();
        await using var connector = environment.CreateConnector();
        var outside = Directory.CreateDirectory(
            Path.Combine(environment.RootDirectory, "outside")).FullName;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => connector.StartTaskAsync(
                new AgentStartRequest(
                    Guid.NewGuid(),
                    environment.ProjectRoot,
                    outside,
                    "TEST_SUCCESS")));
    }

    private static async Task<(AgentStartResult Start, IReadOnlyList<AgentConnectorEvent> Events)>
        StartAndCollectAsync(
            CodexConnector connector,
            ConnectorTestEnvironment environment,
            string instruction)
    {
        var start = await connector.StartTaskAsync(
            environment.NewRequest(Guid.NewGuid(), instruction));
        return (start, await CollectAsync(connector, start.Run));
    }

    private static async Task<IReadOnlyList<AgentConnectorEvent>> CollectAsync(
        CodexConnector connector,
        AgentRunReference run)
    {
        var events = new List<AgentConnectorEvent>();
        await foreach (var connectorEvent in connector.GetTaskEventsAsync(run, 0))
        {
            events.Add(connectorEvent);
        }

        return events;
    }
}

internal sealed class ConnectorTestEnvironment : IAsyncDisposable
{
    private ConnectorTestEnvironment(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        ProjectRoot = Directory.CreateDirectory(Path.Combine(rootDirectory, "project")).FullName;
    }

    public string RootDirectory { get; }

    public string ProjectRoot { get; }

    public static ConnectorTestEnvironment Create()
    {
        var root = Path.Combine(Path.GetTempPath(), $"screen-guide-codex-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new ConnectorTestEnvironment(root);
    }

    public CodexConnector CreateConnector(string? executable = null)
    {
        executable ??= Path.ChangeExtension(typeof(FakeCodexMarker).Assembly.Location, ".exe");
        return new CodexConnector(
            new CodexConnectorOptions(Path.Combine(RootDirectory, "data"), executable));
    }

    public string CopyFakeCliToUnverifiedVersionDirectory()
    {
        var sourceDirectory = Path.GetDirectoryName(typeof(FakeCodexMarker).Assembly.Location)!;
        var destination = Path.Combine(RootDirectory, "unverified-version");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        return Path.Combine(
            destination,
            Path.GetFileName(Path.ChangeExtension(typeof(FakeCodexMarker).Assembly.Location, ".exe")));
    }

    public AgentStartRequest NewRequest(Guid taskId, string instruction) =>
        new(taskId, ProjectRoot, ProjectRoot, instruction, Guid.NewGuid());

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(RootDirectory))
        {
            Directory.Delete(RootDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
