using ScreenGuide.Core.Ai;
using ScreenGuide.Persistence.Sqlite;

namespace ScreenGuide.Tasking.Tests;

public sealed class SqliteAiInvocationStoreTests
{
    [Fact]
    public async Task CompletedInvocationIsTraceableWithoutPersistingPromptOrConversationContent()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var store = new SqliteAiInvocationStore(environment.DatabasePath);
        await store.InitializeAsync();
        var invocationId = Guid.NewGuid();
        var startedAt = new DateTimeOffset(2026, 8, 24, 8, 0, 0, TimeSpan.Zero);

        await store.StartAsync(new AiInvocationRecord
        {
            Id = invocationId,
            Purpose = AiInvocationPurpose.Conversation,
            ProviderId = "provider-a",
            ModelId = "model-a",
            PromptId = "conversation.safe-chat",
            PromptVersion = "v1.0.0",
            PromptContentHash = "sha256:0123456789abcdef",
            DataDestination = "Provider A API",
            Status = AiInvocationStatus.Running,
            StartedAtUtc = startedAt
        });
        var largeInputTokenCount = (long)int.MaxValue + 10;
        await store.CompleteAsync(
            invocationId,
            "stop",
            new AiTokenUsage(largeInputTokenCount, 7, largeInputTokenCount + 7),
            "provider-request-1",
            startedAt.AddSeconds(2));

        var stored = await store.GetAsync(invocationId);

        Assert.NotNull(stored);
        Assert.Equal(AiInvocationStatus.Succeeded, stored.Status);
        Assert.Equal("provider-a", stored.ProviderId);
        Assert.Equal("model-a", stored.ModelId);
        Assert.Equal("conversation.safe-chat", stored.PromptId);
        Assert.Equal("v1.0.0", stored.PromptVersion);
        Assert.Equal("sha256:0123456789abcdef", stored.PromptContentHash);
        Assert.Equal("stop", stored.FinishReason);
        Assert.Equal(
            new AiTokenUsage(largeInputTokenCount, 7, largeInputTokenCount + 7),
            stored.Usage);
        Assert.Equal("provider-request-1", stored.ProviderRequestId);
    }
}
