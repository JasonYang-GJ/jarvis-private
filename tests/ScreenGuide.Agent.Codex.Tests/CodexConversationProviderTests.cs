using ScreenGuide.Agent.Codex;
using ScreenGuide.Core.Conversations;
using ScreenGuide.FakeCodexCli;

namespace ScreenGuide.Agent.Codex.Tests;

public sealed class CodexConversationProviderTests
{
    [Fact]
    public async Task ReturnsStructuredReplyOnlyAfterAuthoritativeCompletionAndResumesThread()
    {
        await using var environment = ConversationProviderEnvironment.Create();
        await using var provider = environment.CreateProvider();
        string? startedThread = null;

        var first = await provider.SendAsync(
            new ConversationProviderRequest(Guid.NewGuid(), Guid.NewGuid(), "ECHO_CHAT_INPUT", null),
            (threadId, processId) =>
            {
                startedThread = threadId;
                Assert.True(processId > 0);
                return Task.CompletedTask;
            });
        var second = await provider.SendAsync(
            new ConversationProviderRequest(Guid.NewGuid(), Guid.NewGuid(), "继续", first.ExternalThreadId));

        Assert.Equal(ConversationProviderOutcome.Succeeded, first.Outcome);
        Assert.Equal("已收到对话正文", first.Reply);
        Assert.Equal(first.ExternalThreadId, startedThread);
        Assert.Equal(first.ExternalThreadId, second.ExternalThreadId);
        Assert.Contains(first.ExternalThreadId!, second.Reply!, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(environment.Workspace, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ProcessExitWithoutTerminalEventIsNeverReportedAsSucceeded()
    {
        await using var environment = ConversationProviderEnvironment.Create();
        await using var provider = environment.CreateProvider(environment.CopyFakeCli("conversation-no-terminal"));

        var result = await provider.SendAsync(new ConversationProviderRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "CONVERSATION_MISSING_TERMINAL",
            null));

        Assert.NotEqual(ConversationProviderOutcome.Succeeded, result.Outcome);
        Assert.Null(result.Reply);
        Assert.Equal("missing_terminal_event", result.FailureCode);
    }

    [Fact]
    public async Task CancellationTerminatesConversationProcessTree()
    {
        await using var environment = ConversationProviderEnvironment.Create();
        await using var provider = environment.CreateProvider();
        var conversationId = Guid.NewGuid();
        var marker = Path.Combine(environment.RootDirectory, "child-must-not-survive.txt");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = provider.SendAsync(
            new ConversationProviderRequest(
                conversationId,
                Guid.NewGuid(),
                $"TEST_LONG_RUNNING\nMARKER={marker}",
                null),
            (threadId, processId) =>
            {
                started.TrySetResult();
                return Task.CompletedTask;
            });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await provider.CancelAsync(conversationId);
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.Equal(ConversationProviderOutcome.Cancelled, result.Outcome);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task SendCancellationTokenTerminatesConversationProcessTree()
    {
        await using var environment = ConversationProviderEnvironment.Create();
        await using var provider = environment.CreateProvider();
        var marker = Path.Combine(environment.RootDirectory, "token-child-must-not-survive.txt");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var running = provider.SendAsync(
            new ConversationProviderRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                $"TEST_LONG_RUNNING\nMARKER={marker}",
                null),
            (threadId, processId) =>
            {
                started.TrySetResult();
                return Task.CompletedTask;
            },
            cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        cancellation.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.Equal(ConversationProviderOutcome.Cancelled, result.Outcome);
        Assert.False(File.Exists(marker));
    }

    private sealed class ConversationProviderEnvironment : IAsyncDisposable
    {
        private ConversationProviderEnvironment(string rootDirectory)
        {
            RootDirectory = rootDirectory;
            DataDirectory = Path.Combine(rootDirectory, "data");
            Workspace = Path.Combine(DataDirectory, "conversation-workspace");
        }

        public string RootDirectory { get; }

        public string DataDirectory { get; }

        public string Workspace { get; }

        public static ConversationProviderEnvironment Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"screen-guide-chat-provider-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return new ConversationProviderEnvironment(root);
        }

        public CodexConversationProvider CreateProvider(string? executable = null) => new(
            new CodexChatOptions(
                DataDirectory,
                executable ?? Path.ChangeExtension(typeof(FakeCodexMarker).Assembly.Location, ".exe")));

        public string CopyFakeCli(string directoryName)
        {
            var sourceDirectory = Path.GetDirectoryName(typeof(FakeCodexMarker).Assembly.Location)!;
            var destination = Path.Combine(RootDirectory, directoryName);
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(sourceDirectory))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            }

            return Path.Combine(
                destination,
                Path.GetFileName(Path.ChangeExtension(typeof(FakeCodexMarker).Assembly.Location, ".exe")));
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
