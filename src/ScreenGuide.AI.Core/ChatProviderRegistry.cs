namespace ScreenGuide.AI.Core;

public sealed record ChatProviderRegistration(
    IChatModelProvider Provider,
    ChatModelDescriptor Model,
    ChatProviderDescriptor ProviderDescriptor);

public sealed class ChatProviderRegistry
{
    private readonly IReadOnlyDictionary<string, ProviderRegistration> _providers;

    public ChatProviderRegistry(IEnumerable<IChatModelProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var registrations = new Dictionary<string, ProviderRegistration>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            var descriptor = provider.Descriptor
                ?? throw new InvalidOperationException("Chat Provider 必须提供描述信息。");
            var providerId = RequireId(descriptor.ProviderId, "Provider ID");
            if (!descriptor.SupportedWorkloads.HasFlag(ChatProviderWorkloads.OrdinaryChat))
            {
                throw new InvalidOperationException(
                    $"Provider“{providerId}”未声明可用于普通聊天，不能注册到 Chat Provider Registry。");
            }

            var models = new Dictionary<string, ChatModelDescriptor>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in descriptor.Models ?? [])
            {
                var modelId = RequireId(model.ModelId, "Model ID");
                if (!models.TryAdd(modelId, model))
                {
                    throw new InvalidOperationException(
                        $"Provider“{providerId}”中的 Model ID 重复：{modelId}");
                }
            }

            if (models.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Provider“{providerId}”必须包含至少一个普通聊天模型。");
            }

            if (!registrations.TryAdd(
                    providerId,
                    new ProviderRegistration(provider, descriptor, models)))
            {
                throw new InvalidOperationException($"Provider ID 重复：{providerId}");
            }
        }

        _providers = registrations;
        Providers = registrations.Values
            .Select(item => item.Descriptor)
            .OrderBy(item => item.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<ChatProviderDescriptor> Providers { get; }

    public ChatProviderRegistration GetRequired(string providerId, string modelId)
    {
        var normalizedProviderId = RequireId(providerId, "Provider ID");
        var normalizedModelId = RequireId(modelId, "Model ID");
        if (!_providers.TryGetValue(normalizedProviderId, out var registration))
        {
            throw new KeyNotFoundException($"未注册 Chat Provider：{normalizedProviderId}");
        }

        if (!registration.Models.TryGetValue(normalizedModelId, out var model))
        {
            throw new KeyNotFoundException(
                $"Provider“{registration.Descriptor.ProviderId}”未注册模型：{normalizedModelId}");
        }

        return new ChatProviderRegistration(
            registration.Provider,
            model,
            registration.Descriptor);
    }

    public IChatModelProvider GetProviderRequired(string providerId)
    {
        var normalizedProviderId = RequireId(providerId, "Provider ID");
        if (!_providers.TryGetValue(normalizedProviderId, out var registration))
        {
            throw new KeyNotFoundException($"未注册 Chat Provider：{normalizedProviderId}");
        }

        return registration.Provider;
    }

    private static string RequireId(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} 不能为空。");
        }

        return value.Trim();
    }

    private sealed record ProviderRegistration(
        IChatModelProvider Provider,
        ChatProviderDescriptor Descriptor,
        IReadOnlyDictionary<string, ChatModelDescriptor> Models);
}
