using System.Collections.Concurrent;
using System.Text.Json;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Sessions;
using ScreenGuide.Core.Tasking;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed record LocalConversationDetails(
    ConversationRecord Conversation,
    IReadOnlyList<ConversationMessageRecord> Messages,
    IReadOnlyList<ConversationTurnRecord> Turns);

public sealed record ConversationSendResult(Guid ConversationId, Guid TurnId, bool WasDuplicate);

/// <summary>
/// Desktop Host 内唯一的对话编排入口。UI 只能经 IPC 调用这里，不能直接接触 Provider 或 SQLite。
/// </summary>
public sealed class ConversationService(
    IConversationStore conversationStore,
    IConversationProvider provider,
    ILocalTaskStore taskStore,
    DesktopHostState hostState,
    TimeProvider timeProvider)
{
    private const int MaximumMessageLength = 20_000;
    private readonly ConcurrentDictionary<Guid, ActiveConversationTurn> _runningTurns = new();
    private readonly ConcurrentDictionary<Guid, ActiveConversationTurn> _activeConversations = new();

    public Task<IReadOnlyList<ConversationRecord>> GetConversationsAsync(
        CancellationToken cancellationToken = default) =>
        conversationStore.GetConversationsAsync(cancellationToken);

    public async Task<LocalConversationDetails?> GetDetailsAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await conversationStore.GetConversationAsync(conversationId, cancellationToken)
            .ConfigureAwait(false);
        if (conversation is null)
        {
            return null;
        }

        var messagesTask = conversationStore.GetMessagesAsync(conversationId, cancellationToken);
        var turnsTask = conversationStore.GetTurnsAsync(conversationId, cancellationToken);
        await Task.WhenAll(messagesTask, turnsTask).ConfigureAwait(false);
        return new LocalConversationDetails(conversation, await messagesTask, await turnsTask);
    }

    public async Task<ConversationRecord> CreateAsync(
        string? title,
        CancellationToken cancellationToken = default)
    {
        var state = RequireStartedHost();
        var now = timeProvider.GetUtcNow();
        var conversation = new ConversationRecord
        {
            Id = Guid.NewGuid(),
            CreatedByDeviceId = state.LocalDevice!.Id,
            Title = NormalizeTitle(title),
            ProviderId = provider.ProviderId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        await conversationStore.CreateConversationAsync(conversation, cancellationToken)
            .ConfigureAwait(false);
        await AppendAuditAsync("ConversationCreated", conversation.Id, AuditOutcome.Success, null, cancellationToken)
            .ConfigureAwait(false);
        return conversation;
    }

    public async Task<ConversationSendResult> SendAsync(
        Guid conversationId,
        string message,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default) =>
        await SendCoreAsync(
                conversationId,
                sessionTurnId: null,
                frozenRoute: null,
                message,
                idempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<ConversationSendResult> SendSessionAsync(
        Guid conversationId,
        Guid sessionTurnId,
        SessionTurnFrozenRoute? frozenRoute,
        string message,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        if (sessionTurnId == Guid.Empty)
        {
            throw new ArgumentException("Session Turn 标识不能为空。", nameof(sessionTurnId));
        }

        return await SendCoreAsync(
                conversationId,
                sessionTurnId,
                frozenRoute,
                message,
                idempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ConversationSendResult> SendCoreAsync(
        Guid conversationId,
        Guid? sessionTurnId,
        SessionTurnFrozenRoute? frozenRoute,
        string message,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        RequireStartedHost();
        var normalized = NormalizeMessage(message);
        var turnId = Guid.NewGuid();
        var registration = await conversationStore.StartTurnAsync(
                conversationId,
                turnId,
                normalized,
                string.IsNullOrWhiteSpace(idempotencyKey) ? Guid.NewGuid().ToString("N") : idempotencyKey.Trim(),
                timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        if (!registration.Accepted)
        {
            return new ConversationSendResult(conversationId, registration.Turn.Id, true);
        }

        var active = new ActiveConversationTurn(
            conversationId,
            registration.Turn.Id,
            cancellationToken);
        if (!_activeConversations.TryAdd(conversationId, active))
        {
            throw new InvalidOperationException("这个会话仍有一条消息正在处理。");
        }

        var run = RunTurnAsync(
            conversationId,
            registration.Turn.Id,
            sessionTurnId,
            frozenRoute,
            normalized,
            active.Cancellation.Token);
        active.SetRun(run);
        _runningTurns[registration.Turn.Id] = active;
        _ = run.ContinueWith(
            completedTask =>
            {
                _runningTurns.TryRemove(registration.Turn.Id, out _);
                _activeConversations.TryRemove(conversationId, out _);
                active.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        await AppendAuditAsync(
                "ConversationTurnStarted",
                conversationId,
                AuditOutcome.Success,
                JsonSerializer.Serialize(new { turnId = registration.Turn.Id }),
                CancellationToken.None)
            .ConfigureAwait(false);
        return new ConversationSendResult(conversationId, registration.Turn.Id, false);
    }

    public async Task CancelAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        RequireStartedHost();
        var conversation = await conversationStore.GetConversationAsync(conversationId, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("对话不存在。");
        if (!_activeConversations.TryGetValue(conversationId, out var active))
        {
            throw new InvalidOperationException(
                conversation.Status == ConversationStatus.Responding
                    ? "当前回答已经结束。"
                    : "当前没有正在生成的回答。");
        }

        Exception? persistenceFailure = null;
        try
        {
            await conversationStore.FailTurnAsync(
                    conversationId,
                    active.TurnId,
                    ConversationTurnStatus.Cancelled,
                    "cancelled",
                    "回答已停止。",
                    timeProvider.GetUtcNow(),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            persistenceFailure = exception;
        }

        active.Cancellation.Cancel();
        await provider.CancelAsync(conversationId, cancellationToken).ConfigureAwait(false);
        await active.Run.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
        if (persistenceFailure is not null)
        {
            throw new InvalidOperationException("无法安全保存回答的取消状态。", persistenceFailure);
        }

        await AppendAuditAsync("ConversationCancelRequested", conversationId, AuditOutcome.Success, null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task WaitForTurnAsync(Guid turnId, CancellationToken cancellationToken = default)
    {
        if (_runningTurns.TryGetValue(turnId, out var running))
        {
            await running.Run.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var active = _activeConversations.Values.ToArray();
        foreach (var turn in active)
        {
            turn.Cancellation.Cancel();
            await provider.CancelAsync(turn.ConversationId, cancellationToken).ConfigureAwait(false);
        }

        var running = active.Select(item => item.Run).ToArray();
        if (running.Length > 0)
        {
            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(12), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RunTurnAsync(
        Guid conversationId,
        Guid turnId,
        Guid? sessionTurnId,
        SessionTurnFrozenRoute? frozenRoute,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            var conversation = await conversationStore.GetConversationAsync(conversationId, CancellationToken.None)
                .ConfigureAwait(false) ?? throw new InvalidOperationException("对话不存在。");
            var result = await provider.SendAsync(
                    new ConversationProviderRequest(
                        conversationId,
                        turnId,
                        message,
                        conversation.ExternalThreadId,
                        sessionTurnId,
                        frozenRoute),
                    (threadId, processId) => conversationStore.RecordProviderStartedAsync(
                        conversationId,
                        turnId,
                        threadId,
                        processId,
                        timeProvider.GetUtcNow(),
                        CancellationToken.None),
                    cancellationToken)
                .ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                result = result with
                {
                    Outcome = ConversationProviderOutcome.Cancelled,
                    Reply = null,
                    FailureCode = "cancelled",
                    FailureMessage = "回答已停止。"
                };
            }

            var completedAt = timeProvider.GetUtcNow();
            if (result.Outcome == ConversationProviderOutcome.Succeeded
                && !string.IsNullOrWhiteSpace(result.Reply))
            {
                await conversationStore.CompleteTurnAsync(
                        conversationId,
                        turnId,
                        result.Reply,
                        result.ProviderMessageId,
                        completedAt,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                await AppendAuditAsync(
                        "ConversationTurnCompleted",
                        conversationId,
                        AuditOutcome.Success,
                        JsonSerializer.Serialize(new { turnId }),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            var status = result.Outcome switch
            {
                ConversationProviderOutcome.Cancelled => ConversationTurnStatus.Cancelled,
                ConversationProviderOutcome.Interrupted => ConversationTurnStatus.Interrupted,
                _ => ConversationTurnStatus.Failed
            };
            await conversationStore.FailTurnAsync(
                    conversationId,
                    turnId,
                    status,
                    result.FailureCode ?? "conversation_failed",
                    FriendlyFailure(result),
                    completedAt,
                    CancellationToken.None)
                .ConfigureAwait(false);
            await AppendAuditAsync(
                    "ConversationTurnEnded",
                    conversationId,
                    status == ConversationTurnStatus.Cancelled ? AuditOutcome.Success : AuditOutcome.Failed,
                    JsonSerializer.Serialize(new { turnId, status = status.ToString(), result.FailureCode }),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await conversationStore.FailTurnAsync(
                        conversationId,
                        turnId,
                        ConversationTurnStatus.Cancelled,
                        "cancelled",
                        "回答已停止。",
                        timeProvider.GetUtcNow(),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
            }
        }
        catch (InvalidOperationException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation may have atomically ended the turn while a successful
            // provider result was waiting to commit. The cancelled terminal state wins.
        }
        catch (Exception exception)
        {
            try
            {
                await conversationStore.FailTurnAsync(
                        conversationId,
                        turnId,
                        ConversationTurnStatus.Failed,
                        "conversation_host_error",
                        FriendlyException(exception),
                        timeProvider.GetUtcNow(),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                await AppendAuditAsync(
                        "ConversationTurnFailed",
                        conversationId,
                        AuditOutcome.Failed,
                        JsonSerializer.Serialize(new { turnId, exceptionType = exception.GetType().Name }),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Provider and persistence failures are already isolated from the Host process.
            }
        }
    }

    private DesktopHostSnapshot RequireStartedHost()
    {
        var snapshot = hostState.Snapshot;
        return snapshot.IsStarted && snapshot.LocalDevice is not null
            ? snapshot
            : throw new InvalidOperationException("Desktop Host 尚未完成启动。");
    }

    private Task AppendAuditAsync(
        string action,
        Guid conversationId,
        AuditOutcome outcome,
        string? details,
        CancellationToken cancellationToken) =>
        taskStore.AppendAuditAsync(
            new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                OccurredAtUtc = timeProvider.GetUtcNow(),
                ActorDeviceId = hostState.Snapshot.LocalDevice?.Id,
                Action = action,
                EntityType = "Conversation",
                EntityId = conversationId.ToString("D"),
                Outcome = outcome,
                DetailsJson = details
            },
            cancellationToken);

    private static string NormalizeTitle(string? title)
    {
        var value = string.IsNullOrWhiteSpace(title) ? "新对话" : title.Trim();
        return value.Length <= 80 ? value : value[..80];
    }

    private static string NormalizeMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("请输入想聊的内容。", nameof(message));
        }

        var value = message.Trim();
        if (value.Length > MaximumMessageLength)
        {
            throw new ArgumentException($"单条消息不能超过 {MaximumMessageLength} 个字符。", nameof(message));
        }

        return value;
    }

    private static string FriendlyFailure(ConversationProviderResult result) => result.Outcome switch
    {
        ConversationProviderOutcome.Cancelled => "回答已停止。",
        ConversationProviderOutcome.Interrupted => "回答意外中断，可以重新发送一条消息继续对话。",
        _ => string.IsNullOrWhiteSpace(result.FailureMessage)
            ? "这次回答没有成功，请稍后再试。"
            : result.FailureMessage
    };

    private static string FriendlyException(Exception exception) => exception switch
    {
        FileNotFoundException => "没有找到 Codex，请先安装并登录 Codex。",
        NotSupportedException => "当前 Codex 版本尚未通过兼容验证。",
        _ => "这次回答没有成功，请稍后再试。"
    };

    private sealed class ActiveConversationTurn(
        Guid conversationId,
        Guid turnId,
        CancellationToken lifetimeToken) : IDisposable
    {
        private readonly TaskCompletionSource<Task> _run =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid ConversationId { get; } = conversationId;

        public Guid TurnId { get; } = turnId;

        public CancellationTokenSource Cancellation { get; } =
            CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);

        public Task Run => _run.Task.Unwrap();

        public void SetRun(Task run) => _run.TrySetResult(run);

        public void Dispose() => Cancellation.Dispose();
    }
}
