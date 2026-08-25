using System.Collections.Concurrent;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Ai;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Sessions;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopHost.Runtime;

/// <summary>
/// Provider-neutral Stage 2 conversation boundary. It executes only the route
/// persisted with the originating Session Turn. Conversation history remains the
/// source of truth; provider-side threads are never required for continuity.
/// </summary>
public sealed class RoutedConversationProvider(
    IConversationStore conversations,
    ModelRouter router,
    PromptRegistry prompts,
    IAiInvocationStore invocations,
    TimeProvider timeProvider) : IConversationProvider
{
    private const string ChatPromptId = "chat.general";
    private const string ChatPromptVersion = "1";
    private const int MaximumHistoryCharacters = 200_000;
    private readonly ConcurrentDictionary<Guid, ActiveRoute> _activeConversations = new();

    public string ProviderId => "model-router";

    public async Task<ConversationProviderResult> SendAsync(
        ConversationProviderRequest request,
        Func<string, int, Task>? started = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SessionTurnId is null || request.FrozenRoute is null)
        {
            return Failed(
                "chat_route_not_frozen",
                "这条历史消息没有保存可执行的 AI 路由，因此没有发送。请重新发送一条新消息。");
        }

        if (request.FrozenRoute.Status == SessionTurnRouteStatus.Unavailable)
        {
            return Failed(
                SensitiveDataSanitizer.DiagnosticCode(
                    request.FrozenRoute.FailureCode,
                    "chat_route_unavailable"),
                "这条消息保存的 AI 路由暂不可用，因此没有发送。请检查 AI 设置后重新发送。");
        }

        FrozenChatModelRoute route;
        try
        {
            if (request.FrozenRoute.Status != SessionTurnRouteStatus.Ready
                || string.IsNullOrWhiteSpace(request.FrozenRoute.ProviderId)
                || string.IsNullOrWhiteSpace(request.FrozenRoute.ModelId)
                || string.IsNullOrWhiteSpace(request.FrozenRoute.DataDestination)
                || request.FrozenRoute.SendsDataOffDevice is null)
            {
                return Failed(
                    "frozen_chat_route_invalid",
                    "这条消息保存的 AI 路由不完整，因此没有发送。请重新发送一条新消息。");
            }

            route = router.RestoreFrozenChatRoute(
                request.FrozenRoute.ProviderId,
                request.FrozenRoute.ModelId,
                request.FrozenRoute.DataDestination,
                request.FrozenRoute.SendsDataOffDevice.Value);
        }
        catch (ChatModelException exception) when (
            string.Equals(exception.Error.Code, "frozen_chat_route_invalid", StringComparison.Ordinal))
        {
            return Failed(exception.Error.Code, SafeProviderMessage(exception.Error.UserMessage));
        }

        var prompt = prompts.GetRequired(ChatPromptId, ChatPromptVersion, route.ProviderId);
        var history = await conversations.GetMessagesAsync(request.ConversationId, cancellationToken)
            .ConfigureAwait(false);
        var messages = BuildHistory(request, history);
        var invocationId = Guid.NewGuid();
        var invocation = new AiInvocationRecord
        {
            Id = invocationId,
            SessionTurnId = request.SessionTurnId.Value,
            ConversationTurnId = request.ConversationTurnId,
            Purpose = AiInvocationPurpose.Conversation,
            ProviderId = route.ProviderId,
            ModelId = route.ModelId,
            PromptId = prompt.PromptId,
            PromptVersion = prompt.Version,
            PromptContentHash = prompt.ContentSha256,
            DataDestination = route.DataDestination,
            Status = AiInvocationStatus.Running,
            StartedAtUtc = timeProvider.GetUtcNow()
        };
        await invocations.StartAsync(invocation, cancellationToken).ConfigureAwait(false);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var active = new ActiveRoute(request.SessionTurnId.Value, linkedCancellation);
        if (!_activeConversations.TryAdd(request.ConversationId, active))
        {
            await invocations.FailAsync(
                    invocationId,
                    AiInvocationStatus.Failed,
                    "conversation_already_active",
                    timeProvider.GetUtcNow(),
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw new InvalidOperationException("这个会话仍有一条消息正在处理。");
        }

        try
        {
            var response = await router.CompleteAsync(
                    route,
                    new ChatModelRequest(
                        invocationId,
                        request.SessionTurnId.Value,
                        route.ModelId,
                        prompt.Content,
                        messages,
                        ResponseFormat: ChatResponseFormat.Text,
                        Prompt: new ChatPromptReference(
                            prompt.PromptId,
                            prompt.Version,
                            prompt.ContentSha256)),
                    cancellationToken: linkedCancellation.Token)
                .ConfigureAwait(false);

            if (linkedCancellation.IsCancellationRequested
                || response.FinishReason == ChatFinishReason.Cancelled)
            {
                var cancelled = await MarkFailedAsync(
                        invocationId,
                        AiInvocationStatus.Cancelled,
                        "cancelled")
                    .ConfigureAwait(false);
                return ResolveTerminal(cancelled, Cancelled());
            }

            if (response.FinishReason == ChatFinishReason.Error
                || string.IsNullOrWhiteSpace(response.Text))
            {
                var failed = await MarkFailedAsync(
                        invocationId,
                        AiInvocationStatus.Failed,
                        "invalid_provider_response")
                    .ConfigureAwait(false);
                return ResolveTerminal(
                    failed,
                    Failed(
                        "invalid_provider_response",
                        "这个 AI 服务没有返回可用回答，请稍后再试。"));
            }

            var terminal = await invocations.CompleteAsync(
                    invocationId,
                    response.FinishReason.ToString(),
                    response.Usage is null
                        ? null
                        : new AiTokenUsage(
                            response.Usage.InputTokens ?? 0,
                            response.Usage.OutputTokens ?? 0,
                            response.Usage.TotalTokens ?? 0),
                    response.Metadata.ProviderRequestId,
                    timeProvider.GetUtcNow(),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return ResolveTerminal(
                terminal,
                new ConversationProviderResult(
                    ConversationProviderOutcome.Succeeded,
                    ExternalThreadId: null,
                    response.Text.Trim(),
                    response.Metadata.ProviderRequestId,
                    ProcessId: null));
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            var cancelled = await MarkFailedAsync(
                    invocationId,
                    AiInvocationStatus.Cancelled,
                    "cancelled")
                .ConfigureAwait(false);
            return ResolveTerminal(cancelled, Cancelled());
        }
        catch (ChatModelException exception)
        {
            var code = SensitiveDataSanitizer.DiagnosticCode(
                exception.Error.Code,
                "chat_provider_error");
            var status = exception.Error.Kind == ChatModelErrorKind.Cancelled
                ? AiInvocationStatus.Cancelled
                : AiInvocationStatus.Failed;
            var terminal = await MarkFailedAsync(invocationId, status, code).ConfigureAwait(false);
            var requestedResult = status == AiInvocationStatus.Cancelled
                ? Cancelled()
                : Failed(code, SafeProviderMessage(exception.Error.UserMessage));
            return ResolveTerminal(terminal, requestedResult);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException
            && exception is not StackOverflowException)
        {
            var failed = await MarkFailedAsync(
                    invocationId,
                    AiInvocationStatus.Failed,
                    "chat_provider_error")
                .ConfigureAwait(false);
            return ResolveTerminal(
                failed,
                Failed("chat_provider_error", "这次回答没有成功，请稍后再试。"));
        }
        finally
        {
            if (_activeConversations.TryGetValue(request.ConversationId, out var current)
                && ReferenceEquals(current, active))
            {
                _activeConversations.TryRemove(request.ConversationId, out _);
            }
        }
    }

    public async Task CancelAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_activeConversations.TryGetValue(conversationId, out var active))
        {
            return;
        }

        await router.CancelAsync(active.TurnId, CancellationToken.None).ConfigureAwait(false);
        active.Cancellation.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        var active = _activeConversations.Values.ToArray();
        foreach (var item in active)
        {
            await router.CancelAsync(item.TurnId, CancellationToken.None).ConfigureAwait(false);
            item.Cancellation.Cancel();
        }

        _activeConversations.Clear();
    }

    private Task<AiInvocationTransitionResult> MarkFailedAsync(
        Guid invocationId,
        AiInvocationStatus status,
        string code) =>
        invocations.FailAsync(
            invocationId,
            status,
            code,
            timeProvider.GetUtcNow(),
            CancellationToken.None);

    private static IReadOnlyList<ChatMessage> BuildHistory(
        ConversationProviderRequest request,
        IReadOnlyList<ConversationMessageRecord> history)
    {
        if (history.Count == 0
            || history[^1].Role != ConversationMessageRole.User
            || !string.Equals(history[^1].Content, request.Message, StringComparison.Ordinal))
        {
            throw new InvalidDataException("当前对话历史与正在处理的消息不一致。");
        }

        var characterCount = 0;
        var messages = new ChatMessage[history.Count];
        for (var index = 0; index < history.Count; index++)
        {
            var item = history[index];
            if (item.ConversationId != request.ConversationId)
            {
                throw new InvalidDataException("对话历史包含其他会话的消息。");
            }

            characterCount = checked(characterCount + item.Content.Length);
            if (characterCount > MaximumHistoryCharacters)
            {
                throw new ChatModelException(
                    "model-router",
                    null,
                    new ChatModelError(
                        ChatModelErrorKind.InvalidRequest,
                        "conversation_context_too_large",
                        "当前会话内容过长，请新建一个会话后继续。",
                        IsRetryable: false));
            }

            messages[index] = new ChatMessage(
                item.Role switch
                {
                    ConversationMessageRole.User => ChatMessageRole.User,
                    ConversationMessageRole.Assistant => ChatMessageRole.Assistant,
                    ConversationMessageRole.System => ChatMessageRole.System,
                    _ => throw new InvalidDataException("对话消息角色无效。")
                },
                item.Content);
        }

        return messages;
    }

    private static ConversationProviderResult Cancelled() => new(
        ConversationProviderOutcome.Cancelled,
        ExternalThreadId: null,
        Reply: null,
        ProviderMessageId: null,
        ProcessId: null,
        "cancelled",
        "回答已停止。");

    private static ConversationProviderResult Interrupted(string? failureCode = null) => new(
        ConversationProviderOutcome.Interrupted,
        ExternalThreadId: null,
        Reply: null,
        ProviderMessageId: null,
        ProcessId: null,
        SensitiveDataSanitizer.DiagnosticCode(failureCode, "host_restarted"),
        "主程序已重新启动，这次回答已中断。请重新发送。");

    private static ConversationProviderResult Failed(string code, string message) => new(
        ConversationProviderOutcome.Failed,
        ExternalThreadId: null,
        Reply: null,
        ProviderMessageId: null,
        ProcessId: null,
        code,
        message);

    private static ConversationProviderResult ResolveTerminal(
        AiInvocationTransitionResult transition,
        ConversationProviderResult requestedResult)
    {
        if (transition.RequestedStatusWon)
        {
            return requestedResult;
        }

        return transition.Current.Status switch
        {
            AiInvocationStatus.Cancelled => Cancelled(),
            AiInvocationStatus.Interrupted => Interrupted(transition.Current.FailureCode),
            AiInvocationStatus.Failed => Failed(
                SensitiveDataSanitizer.DiagnosticCode(
                    transition.Current.FailureCode,
                    "chat_provider_error"),
                "这次回答没有成功，请稍后再试。"),
            AiInvocationStatus.Succeeded => requestedResult,
            _ => Failed(
                "invocation_terminal_conflict",
                "这次回答的状态无法确认，请重新发送。")
        };
    }

    private static string SafeProviderMessage(string? message)
    {
        var redacted = SensitiveDataSanitizer.Redact(message);
        if (string.IsNullOrWhiteSpace(redacted))
        {
            return "这次回答没有成功，请稍后再试。";
        }

        return redacted.Length <= 300 ? redacted : redacted[..300];
    }

    private sealed record ActiveRoute(Guid TurnId, CancellationTokenSource Cancellation);
}
