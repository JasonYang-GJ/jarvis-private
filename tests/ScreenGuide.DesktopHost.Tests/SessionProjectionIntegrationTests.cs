using ScreenGuide.DesktopProtocol;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Sessions;
using ScreenGuide.DesktopHost.Runtime;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class SessionProjectionIntegrationTests
{
    [Fact]
    public async Task WaitReturnsNoChangeDeltaAndResetWithoutRepeatingTheBootstrap()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var bootstrap = await client.StartNewSessionAsync("有界投影");

        var noChange = await client.WaitForSessionProjectionAsync(new SessionProjectionCursorDto(
            bootstrap.CoordinatorInstanceId,
            bootstrap.CoordinatorStartedAtUtc,
            bootstrap.SessionId,
            bootstrap.ChangeVersion,
            KnownMessageSequenceNumber: 0,
            WaitMilliseconds: 20));

        Assert.Equal("NoChange", noChange.Kind);
        Assert.Empty(noChange.TurnUpserts);
        Assert.Empty(noChange.MessageUpserts);
        Assert.Null(noChange.Bootstrap);

        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "打开记事本",
            "Text",
            "projection-delta",
            bootstrap.SessionId));
        var delta = await client.WaitForSessionProjectionAsync(new SessionProjectionCursorDto(
            bootstrap.CoordinatorInstanceId,
            bootstrap.CoordinatorStartedAtUtc,
            bootstrap.SessionId,
            bootstrap.ChangeVersion,
            KnownMessageSequenceNumber: 0,
            WaitMilliseconds: 2_000));

        Assert.Equal("Delta", delta.Kind);
        Assert.InRange(delta.TurnUpserts.Count, 1, 32);
        Assert.Contains(delta.TurnUpserts, item => item.Id == submitted.TurnId);
        Assert.InRange(delta.MessageUpserts.Count, 0, 50);
        Assert.Null(delta.Bootstrap);

        var staleHost = await client.WaitForSessionProjectionAsync(new SessionProjectionCursorDto(
            "stale-host-instance",
            bootstrap.CoordinatorStartedAtUtc.AddMinutes(-1),
            bootstrap.SessionId,
            delta.ChangeVersion,
            delta.LastMessageSequenceNumber,
            WaitMilliseconds: 0));
        await host.StopAsync();

        Assert.Equal("ResetRequired", staleHost.Kind);
        Assert.Equal("coordinator_changed", staleHost.ResetReason);
        Assert.Empty(staleHost.TurnUpserts);
        Assert.Empty(staleHost.MessageUpserts);
    }

    [Fact]
    public async Task MessagePagesAndExactTurnStayBoundToTheRequestedSession()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var first = await client.StartNewSessionAsync("第一页");
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "打开记事本",
            "Text",
            "projection-exact-turn",
            first.SessionId));
        var exact = await client.GetSessionTurnAsync(first.SessionId, submitted.TurnId);
        var page = await client.GetSessionMessagesPageAsync(
            first.SessionId,
            beforeSequenceNumber: null,
            pageSize: 50);
        var second = await client.StartNewSessionAsync("第二页");

        var wrongSession = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.GetSessionTurnAsync(second.SessionId, submitted.TurnId));
        await host.StopAsync();

        Assert.Equal(submitted.TurnId, exact.Turn.Id);
        Assert.Equal(first.SessionId, exact.SessionId);
        Assert.All(page.Messages, message => Assert.True(message.SequenceNumber > 0));
        Assert.Equal("session_turn_not_found", wrongSession.Error.Code);
    }

    [Fact]
    public async Task BootstrapUsesBoundedActiveQueryAcrossOneThousandTurnsAndExactTurnRemainsAuthoritative()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("统一有界 Turn 视图");
        var store = host.Services.GetRequiredService<ISessionStore>();
        var now = environment.TimeProvider.GetUtcNow();
        var created = new List<SessionTurnRecord>();
        for (var sequence = 1; sequence <= 1_000; sequence++)
        {
            var registration = await store.StartTurnAsync(
                session.SessionId,
                $"turn-{sequence}",
                "Text",
                $"projection-bound-{sequence}",
                ReadyRoute(now.AddSeconds(sequence)),
                now.AddSeconds(sequence));
            var turn = registration.Turn;
            if (sequence > 1)
            {
                turn = await store.UpdateTurnAsync(
                    turn with
                    {
                        WorkKind = SessionWorkKind.CodingTask,
                        Phase = SessionTurnPhase.ProgrammingTask
                    },
                    turn.Version,
                    now.AddSeconds(sequence).AddMilliseconds(1));
            }

            created.Add(turn);
        }

        var bootstrap = await client.GetCurrentSessionAsync();
        var exactOmitted = await client.GetSessionTurnAsync(session.SessionId, created[1].Id);
        await host.StopAsync();

        Assert.NotNull(bootstrap);
        var allProjected = bootstrap!.Turns.Concat(bootstrap.ActiveTurns).ToArray();
        Assert.Equal(32, allProjected.Select(item => item.Id).Distinct().Count());
        Assert.Empty(bootstrap.Turns.Select(item => item.Id).Intersect(
            bootstrap.ActiveTurns.Select(item => item.Id)));
        Assert.Equal(created[0].Id, bootstrap.ForegroundTurn?.Id);
        Assert.Equal(
            new[] { 1 }.Concat(Enumerable.Range(970, 31)),
            allProjected.Select(item => item.SequenceNumber).Order());
        Assert.Equal(created[1].Id, exactOmitted.Turn.Id);
        Assert.Equal(2, exactOmitted.Turn.SequenceNumber);
    }

    [Fact]
    public async Task MessagePageShrinksSerializedMultiItemPayloadWithoutTruncatingContent()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("自适应历史页");
        var conversationStore = host.Services.GetRequiredService<IConversationStore>();
        var content = new string('界', 12_000);
        for (var index = 0; index < 25; index++)
        {
            var turnId = Guid.NewGuid();
            _ = await conversationStore.StartTurnAsync(
                session.ConversationId,
                turnId,
                $"{index:D2}-{content}",
                $"projection-budget-{index}",
                environment.TimeProvider.GetUtcNow());
            await conversationStore.CompleteTurnAsync(
                session.ConversationId,
                turnId,
                $"{index:D2}-{content}",
                null,
                environment.TimeProvider.GetUtcNow());
        }

        var boundedBootstrap = await client.GetCurrentSessionAsync();
        var page = await client.GetSessionMessagesPageAsync(
            session.SessionId,
            beforeSequenceNumber: null,
            pageSize: 50);
        await host.StopAsync();

        Assert.NotNull(boundedBootstrap);
        Assert.InRange(boundedBootstrap.Messages.Count, 1, 49);
        Assert.True(boundedBootstrap.HasEarlierMessages);
        Assert.InRange(
            JsonSerializer.SerializeToUtf8Bytes(boundedBootstrap, DesktopProtocolJson.Options).Length,
            1,
            512 * 1024);
        Assert.InRange(page.Messages.Count, 1, 49);
        Assert.True(page.HasMore);
        Assert.Equal(page.Messages[0].SequenceNumber, page.NextBeforeSequenceNumber);
        Assert.All(page.Messages, message => Assert.EndsWith(content, message.Content, StringComparison.Ordinal));
        Assert.InRange(
            JsonSerializer.SerializeToUtf8Bytes(page, DesktopProtocolJson.Options).Length,
            1,
            512 * 1024);
    }

    [Fact]
    public async Task ProjectionDeltaContinuesSameVersionUntilEveryBudgetedMessageArrives()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var bootstrap = await client.StartNewSessionAsync("投影分批续传");
        var conversationStore = host.Services.GetRequiredService<IConversationStore>();
        var sessionStore = host.Services.GetRequiredService<ISessionStore>();
        var projections = host.Services.GetRequiredService<SessionProjectionService>();
        var content = new string('续', 12_000);
        for (var index = 0; index < 25; index++)
        {
            var conversationTurnId = Guid.NewGuid();
            _ = await conversationStore.StartTurnAsync(
                bootstrap.ConversationId,
                conversationTurnId,
                $"{index:D2}-{content}",
                $"projection-continuation-{index}",
                environment.TimeProvider.GetUtcNow());
            await conversationStore.CompleteTurnAsync(
                bootstrap.ConversationId,
                conversationTurnId,
                $"{index:D2}-{content}",
                null,
                environment.TimeProvider.GetUtcNow());
        }

        var turn = (await sessionStore.StartTurnAsync(
            bootstrap.SessionId,
            "投影唤醒",
            "Text",
            "projection-continuation-wakeup",
            ReadyRoute(environment.TimeProvider.GetUtcNow()),
            environment.TimeProvider.GetUtcNow())).Turn;
        projections.RecordChange(bootstrap.SessionId, turn);

        var seen = new HashSet<long>();
        var version = bootstrap.ChangeVersion;
        var messageSequence = 0L;
        for (var batchIndex = 0; batchIndex < 10 && seen.Count < 50; batchIndex++)
        {
            var update = await client.WaitForSessionProjectionAsync(new SessionProjectionCursorDto(
                bootstrap.CoordinatorInstanceId,
                bootstrap.CoordinatorStartedAtUtc,
                bootstrap.SessionId,
                version,
                messageSequence,
                WaitMilliseconds: 0));
            Assert.Equal("Delta", update.Kind);
            Assert.InRange(
                JsonSerializer.SerializeToUtf8Bytes(update, DesktopProtocolJson.Options).Length,
                1,
                512 * 1024);
            Assert.All(update.MessageUpserts, message => Assert.True(seen.Add(message.SequenceNumber)));
            version = update.ChangeVersion;
            messageSequence = update.LastMessageSequenceNumber;
        }

        await host.StopAsync();
        Assert.Equal(50, seen.Count);
        Assert.Equal(Enumerable.Range(1, 50).Select(value => (long)value), seen.Order());
    }

    [Fact]
    public async Task ProjectionDeltaResetsWhenLegalTurnAndMessageItemsCannotFitTogether()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var bootstrap = await client.StartNewSessionAsync("投影聚合预算");
        var sessionStore = host.Services.GetRequiredService<ISessionStore>();
        var conversationStore = host.Services.GetRequiredService<IConversationStore>();
        var projections = host.Services.GetRequiredService<SessionProjectionService>();
        var now = environment.TimeProvider.GetUtcNow();
        var largeTurnInput = new string('T', 180_000);
        var largeMessage = new string('M', 180_000);

        for (var index = 0; index < 2; index++)
        {
            var turn = (await sessionStore.StartTurnAsync(
                bootstrap.SessionId,
                largeTurnInput,
                "Text",
                $"projection-aggregate-budget-{index}",
                ReadyRoute(now),
                now)).Turn;
            projections.RecordChange(bootstrap.SessionId, turn);
        }

        _ = await conversationStore.StartTurnAsync(
            bootstrap.ConversationId,
            Guid.NewGuid(),
            largeMessage,
            "projection-aggregate-budget-message",
            now);

        var update = await client.WaitForSessionProjectionAsync(new SessionProjectionCursorDto(
            bootstrap.CoordinatorInstanceId,
            bootstrap.CoordinatorStartedAtUtc,
            bootstrap.SessionId,
            bootstrap.ChangeVersion,
            KnownMessageSequenceNumber: 0,
            WaitMilliseconds: 0));
        await host.StopAsync();

        Assert.Equal("ResetRequired", update.Kind);
        Assert.Equal("projection_budget", update.ResetReason);
        Assert.Equal(bootstrap.SessionId, update.SessionId);
        Assert.Empty(update.TurnUpserts);
        Assert.Empty(update.MessageUpserts);
        Assert.InRange(
            JsonSerializer.SerializeToUtf8Bytes(update, DesktopProtocolJson.Options).Length,
            1,
            512 * 1024);
    }

    [Fact]
    public async Task ProjectionDeltaRejectsAnIndividuallyOversizedTurnInsideAMultiTurnBatch()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var bootstrap = await client.StartNewSessionAsync("投影单项预算");
        var sessionStore = host.Services.GetRequiredService<ISessionStore>();
        var projections = host.Services.GetRequiredService<SessionProjectionService>();
        var now = environment.TimeProvider.GetUtcNow();

        foreach (var input in new[] { new string('X', 600_000), "small-turn" })
        {
            var turn = (await sessionStore.StartTurnAsync(
                bootstrap.SessionId,
                input,
                "Text",
                $"projection-single-item-budget-{Guid.NewGuid():N}",
                ReadyRoute(now),
                now)).Turn;
            projections.RecordChange(bootstrap.SessionId, turn);
        }

        var exception = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.WaitForSessionProjectionAsync(new SessionProjectionCursorDto(
                bootstrap.CoordinatorInstanceId,
                bootstrap.CoordinatorStartedAtUtc,
                bootstrap.SessionId,
                bootstrap.ChangeVersion,
                KnownMessageSequenceNumber: 0,
                WaitMilliseconds: 0)));
        await host.StopAsync();

        Assert.Equal("session_projection_item_too_large", exception.Error.Code);
    }

    [Fact]
    public void DesktopProtocolIsVersionElevenWithoutChangingTheSchemaContract()
    {
        Assert.Equal(11, DesktopProtocolVersion.Current);
        Assert.Equal(11, DesktopProtocolVersion.MinimumSupported);
        Assert.Equal(11, ScreenGuide.Core.Tasking.V02Contract.SchemaVersion);
    }

    [Fact]
    public void JournalGapsSessionSwitchesAndMoreThanThirtyTwoUpsertsRequireReset()
    {
        var sessionId = Guid.NewGuid();
        var gap = new SessionChangeJournal();
        gap.AppendTurn(3, Turn(sessionId, 1));
        Assert.Equal("journal_gap", gap.Read(1, 3, sessionId).ResetReason);

        var switched = new SessionChangeJournal();
        switched.AppendTurn(2, Turn(Guid.NewGuid(), 1));
        Assert.Equal("projection_invalidated", switched.Read(1, 2, sessionId).ResetReason);

        var overflow = new SessionChangeJournal();
        for (var index = 1; index <= 33; index++)
        {
            overflow.AppendTurn(index + 1, Turn(sessionId, index));
        }

        Assert.Equal("turn_upsert_overflow", overflow.Read(1, 34, sessionId).ResetReason);
    }

    private static SessionTurnRecord Turn(Guid sessionId, int sequenceNumber) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = sessionId,
        SequenceNumber = sequenceNumber,
        InputText = $"turn-{sequenceNumber}",
        InputModality = "Text",
        IdempotencyKey = $"key-{sequenceNumber}",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };

    private static SessionTurnFrozenRoute ReadyRoute(DateTimeOffset frozenAtUtc) => new()
    {
        Status = SessionTurnRouteStatus.Ready,
        ProviderId = "fake",
        ModelId = "fake-model",
        DataDestination = "https://example.invalid",
        SendsDataOffDevice = false,
        FrozenAtUtc = frozenAtUtc
    };
}
