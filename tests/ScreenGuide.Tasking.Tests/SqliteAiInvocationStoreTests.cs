using ScreenGuide.Core.Ai;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Sessions;
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
        var (sessionTurnId, conversationTurnId) = await CreateLinkedTurnsAsync(
            environment,
            startedAt);

        await store.StartAsync(new AiInvocationRecord
        {
            Id = invocationId,
            SessionTurnId = sessionTurnId,
            ConversationTurnId = conversationTurnId,
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
        Assert.Equal(sessionTurnId, stored.SessionTurnId);
        Assert.Equal(conversationTurnId, stored.ConversationTurnId);
        Assert.Equal(invocationId, Assert.Single(
            await store.GetForSessionTurnAsync(sessionTurnId)).Id);
        Assert.Equal(invocationId, Assert.Single(
            await store.GetForConversationTurnAsync(conversationTurnId)).Id);
    }

    [Fact]
    public async Task FirstTerminalStateWinsAndDuplicateTerminalCompletionIsIdempotent()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var store = new SqliteAiInvocationStore(environment.DatabasePath);
        await store.InitializeAsync();
        var startedAt = new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
        var invocation = CreateRunningInvocation(startedAt);
        await store.StartAsync(invocation);

        var succeeded = await store.CompleteAsync(
            invocation.Id,
            "stop",
            new AiTokenUsage(3, 2, 5),
            "request-first",
            startedAt.AddSeconds(1));
        var lateCancelled = await store.FailAsync(
            invocation.Id,
            AiInvocationStatus.Cancelled,
            "cancelled",
            startedAt.AddSeconds(2));
        var duplicateSucceeded = await store.CompleteAsync(
            invocation.Id,
            "late-stop",
            new AiTokenUsage(100, 100, 200),
            "request-late",
            startedAt.AddSeconds(3));

        Assert.Equal(AiInvocationTransitionDisposition.Applied, succeeded.Disposition);
        Assert.True(succeeded.RequestedStatusWon);
        Assert.Equal(AiInvocationTransitionDisposition.RejectedByExistingTerminal,
            lateCancelled.Disposition);
        Assert.False(lateCancelled.RequestedStatusWon);
        Assert.Equal(AiInvocationStatus.Succeeded, lateCancelled.Current.Status);
        Assert.Equal(AiInvocationTransitionDisposition.AlreadyInRequestedTerminal,
            duplicateSucceeded.Disposition);
        Assert.True(duplicateSucceeded.RequestedStatusWon);
        Assert.Equal("stop", duplicateSucceeded.Current.FinishReason);
        Assert.Equal("request-first", duplicateSucceeded.Current.ProviderRequestId);
        Assert.Equal(startedAt.AddSeconds(1), duplicateSucceeded.Current.CompletedAtUtc);
    }

    [Fact]
    public async Task CompetingDifferentTerminalStatesProduceOneAtomicWinner()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var store = new SqliteAiInvocationStore(environment.DatabasePath);
        await store.InitializeAsync();
        var startedAt = new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);
        var invocation = CreateRunningInvocation(startedAt);
        await store.StartAsync(invocation);

        var results = await Task.WhenAll(
            store.CompleteAsync(
                invocation.Id,
                "stop",
                null,
                null,
                startedAt.AddSeconds(1)),
            store.FailAsync(
                invocation.Id,
                AiInvocationStatus.Cancelled,
                "cancelled",
                startedAt.AddSeconds(1)));

        Assert.Single(results, result =>
            result.Disposition == AiInvocationTransitionDisposition.Applied);
        Assert.Single(results, result =>
            result.Disposition == AiInvocationTransitionDisposition.RejectedByExistingTerminal);
        var stored = await store.GetAsync(invocation.Id);
        Assert.NotNull(stored);
        Assert.All(results, result => Assert.Equal(stored.Status, result.Current.Status));
    }

    [Fact]
    public async Task CancellationWinsAgainstLateSuccessAndDuplicateCancellationKeepsOriginalData()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var store = new SqliteAiInvocationStore(environment.DatabasePath);
        await store.InitializeAsync();
        var startedAt = new DateTimeOffset(2026, 8, 24, 10, 30, 0, TimeSpan.Zero);
        var invocation = CreateRunningInvocation(startedAt);
        await store.StartAsync(invocation);

        var cancelled = await store.FailAsync(
            invocation.Id,
            AiInvocationStatus.Cancelled,
            "cancelled",
            startedAt.AddSeconds(1));
        var lateSuccess = await store.CompleteAsync(
            invocation.Id,
            "stop",
            new AiTokenUsage(20, 10, 30),
            "late-request",
            startedAt.AddSeconds(2));
        var duplicateCancelled = await store.FailAsync(
            invocation.Id,
            AiInvocationStatus.Cancelled,
            "different-code-must-not-win",
            startedAt.AddSeconds(3));

        Assert.Equal(AiInvocationTransitionDisposition.Applied, cancelled.Disposition);
        Assert.Equal(AiInvocationTransitionDisposition.RejectedByExistingTerminal,
            lateSuccess.Disposition);
        Assert.False(lateSuccess.RequestedStatusWon);
        Assert.Equal(AiInvocationTransitionDisposition.AlreadyInRequestedTerminal,
            duplicateCancelled.Disposition);
        Assert.True(duplicateCancelled.RequestedStatusWon);
        Assert.Equal("cancelled", duplicateCancelled.Current.FailureCode);
        Assert.Equal(startedAt.AddSeconds(1), duplicateCancelled.Current.CompletedAtUtc);
        Assert.Null(duplicateCancelled.Current.FinishReason);
        Assert.Null(duplicateCancelled.Current.ProviderRequestId);
    }

    [Fact]
    public async Task MissingInvocationRaisesTypedIntegrityFailure()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var store = new SqliteAiInvocationStore(environment.DatabasePath);
        await store.InitializeAsync();
        var missingId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<AiInvocationNotFoundException>(() =>
            store.FailAsync(
                missingId,
                AiInvocationStatus.Interrupted,
                "host_restarted",
                DateTimeOffset.UtcNow));

        Assert.Equal(missingId, exception.InvocationId);
    }

    [Fact]
    public async Task RecoveryInterruptsEveryRunningInvocationOnceIncludingHistoricalNullAssociation()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var store = new SqliteAiInvocationStore(environment.DatabasePath);
        await store.InitializeAsync();
        var startedAt = new DateTimeOffset(2026, 8, 24, 11, 0, 0, TimeSpan.Zero);
        var (sessionTurnId, _) = await CreateLinkedTurnsAsync(environment, startedAt);
        var linked = CreateRunningInvocation(startedAt) with { SessionTurnId = sessionTurnId };
        var historical = CreateRunningInvocation(startedAt.AddSeconds(1));
        await store.StartAsync(linked);
        await store.StartAsync(historical);
        var alreadyCompleted = CreateRunningInvocation(startedAt.AddSeconds(2));
        await store.StartAsync(alreadyCompleted);
        await store.CompleteAsync(
            alreadyCompleted.Id,
            "stop",
            null,
            null,
            startedAt.AddSeconds(3));
        var recoveredAt = startedAt.AddMinutes(1);

        var first = await store.InterruptRunningAsync(
            recoveredAt,
            "host_restarted");
        var second = await store.InterruptRunningAsync(
            recoveredAt.AddSeconds(1),
            "host_restarted");

        Assert.Equal(
            new[] { linked.Id, historical.Id }.Order().ToArray(),
            first.InterruptedInvocationIds.Order().ToArray());
        Assert.Empty(second.InterruptedInvocationIds);
        foreach (var invocationId in first.InterruptedInvocationIds)
        {
            var recovered = await store.GetAsync(invocationId);
            Assert.NotNull(recovered);
            Assert.Equal(AiInvocationStatus.Interrupted, recovered.Status);
            Assert.Equal(recoveredAt, recovered.CompletedAtUtc);
            Assert.Equal("host_restarted", recovered.FailureCode);

            var lateSuccess = await store.CompleteAsync(
                invocationId,
                "stop",
                null,
                "late-request",
                recoveredAt.AddSeconds(2));
            var duplicateInterrupted = await store.FailAsync(
                invocationId,
                AiInvocationStatus.Interrupted,
                "different-code-must-not-win",
                recoveredAt.AddSeconds(3));
            Assert.Equal(AiInvocationTransitionDisposition.RejectedByExistingTerminal,
                lateSuccess.Disposition);
            Assert.Equal(AiInvocationStatus.Interrupted, lateSuccess.Current.Status);
            Assert.Equal(AiInvocationTransitionDisposition.AlreadyInRequestedTerminal,
                duplicateInterrupted.Disposition);
            Assert.Equal("host_restarted", duplicateInterrupted.Current.FailureCode);
            Assert.Equal(recoveredAt, duplicateInterrupted.Current.CompletedAtUtc);
        }

        Assert.Equal(AiInvocationStatus.Succeeded,
            (await store.GetAsync(alreadyCompleted.Id))?.Status);
    }

    private static AiInvocationRecord CreateRunningInvocation(DateTimeOffset startedAt) => new()
    {
        Id = Guid.NewGuid(),
        Purpose = AiInvocationPurpose.Conversation,
        ProviderId = "provider-a",
        ModelId = "model-a",
        PromptId = "chat.general",
        PromptVersion = "1",
        PromptContentHash = "sha256:test",
        DataDestination = "Provider A API",
        Status = AiInvocationStatus.Running,
        StartedAtUtc = startedAt
    };

    private static async Task<(Guid SessionTurnId, Guid ConversationTurnId)> CreateLinkedTurnsAsync(
        TaskStoreTestEnvironment environment,
        DateTimeOffset now)
    {
        var conversationStore = new SqliteConversationStore(environment.DatabasePath);
        await conversationStore.InitializeAsync();
        var conversation = new ConversationRecord
        {
            Id = Guid.NewGuid(),
            CreatedByDeviceId = environment.Device.Id,
            Title = "Invocation association",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        await conversationStore.CreateConversationAsync(conversation);
        var conversationTurn = await conversationStore.StartTurnAsync(
            conversation.Id,
            Guid.NewGuid(),
            "关联测试",
            $"invocation-conversation-{Guid.NewGuid():N}",
            now);

        var sessionStore = new SqliteSessionStore(environment.DatabasePath);
        await sessionStore.InitializeAsync();
        var session = new SessionRecord
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            CreatedByDeviceId = environment.Device.Id,
            Title = "Invocation association",
            IsCurrent = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastActiveAtUtc = now
        };
        await sessionStore.CreateSessionAsync(session);
        var sessionTurn = await sessionStore.StartTurnAsync(
            session.Id,
            "关联测试",
            "Text",
            $"invocation-session-{Guid.NewGuid():N}",
            new SessionTurnFrozenRoute
            {
                Status = SessionTurnRouteStatus.Ready,
                ProviderId = "provider-a",
                ModelId = "model-a",
                DataDestination = "Provider A API",
                SendsDataOffDevice = true,
                FrozenAtUtc = now
            },
            now);

        return (sessionTurn.Turn.Id, conversationTurn.Turn.Id);
    }
}
