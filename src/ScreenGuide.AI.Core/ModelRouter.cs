using System.Collections.Concurrent;

namespace ScreenGuide.AI.Core;

public sealed record FrozenChatModelRoute(
    string ProviderId,
    string ModelId,
    ChatModelCapabilities Capabilities,
    int? ContextWindowTokens,
    string DataDestination,
    bool SendsDataOffDevice);

public sealed class ModelRouter(
    ChatProviderRegistry providers,
    IAiSettingsStore settingsStore)
{
    private readonly ConcurrentDictionary<Guid, IChatModelProvider> _activeTurns = new();

    public async Task<FrozenChatModelRoute> FreezeDefaultChatRouteAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        ChatProviderRegistration registration;
        try
        {
            registration = providers.GetRequired(
                settings.DefaultChatRoute.ProviderId,
                settings.DefaultChatRoute.ModelId);
        }
        catch (KeyNotFoundException)
        {
            throw new ChatModelException(
                settings.DefaultChatRoute.ProviderId,
                settings.DefaultChatRoute.ModelId,
                new ChatModelError(
                    ChatModelErrorKind.ModelNotFound,
                    "configured_chat_model_not_found",
                    "默认聊天模型不可用，请在设置中重新选择 Provider 和模型。",
                    IsRetryable: false));
        }

        return new FrozenChatModelRoute(
            registration.Provider.Descriptor.ProviderId,
            registration.Model.ModelId,
            registration.Model.Capabilities,
            registration.Model.ContextWindowTokens,
            registration.Provider.Descriptor.DataDestination,
            registration.Provider.Descriptor.SendsDataOffDevice);
    }

    public async Task<ChatModelResponse> CompleteAsync(
        FrozenChatModelRoute route,
        ChatModelRequest request,
        ChatModelStreamCallback? streamCallback = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(route.ModelId, request.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("请求模型与已冻结的模型路由不一致。");
        }

        var registration = providers.GetRequired(route.ProviderId, route.ModelId);
        if (!_activeTurns.TryAdd(request.TurnId, registration.Provider))
        {
            throw new InvalidOperationException("同一个 Turn 已经有正在运行的模型请求。");
        }

        try
        {
            return await registration.Provider.CompleteAsync(
                    request,
                    streamCallback,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _activeTurns.TryRemove(request.TurnId, out _);
        }
    }

    public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _activeTurns.TryGetValue(turnId, out var provider)
            ? provider.CancelAsync(turnId, cancellationToken)
            : Task.CompletedTask;
    }
}
