using System.Collections.Concurrent;
using ScreenGuide.AI.Core;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed class AiSettingsService(
    ChatProviderRegistry providers,
    IAiSettingsStore settingsStore,
    IProviderCredentialStore credentials)
{
    private const int MaximumCredentialCharacters = 8 * 1024;
    private readonly ConcurrentDictionary<string, ChatProviderHealth> _health =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<AiSettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        EnsureRegisteredRoute(settings.DefaultChatRoute);
        var providerSettings = new List<AiProviderSettingsDto>(providers.Providers.Count);
        foreach (var descriptor in providers.Providers)
        {
            var credentialStatus = await GetCredentialStatusAsync(descriptor, cancellationToken)
                .ConfigureAwait(false);
            var health = CurrentHealth(descriptor, credentialStatus);
            providerSettings.Add(new AiProviderSettingsDto(
                descriptor.ProviderId,
                descriptor.DisplayName,
                descriptor.DataDestination,
                descriptor.SendsDataOffDevice,
                ConfigurationState(descriptor, credentialStatus),
                MapHealth(health),
                descriptor.Models.Select(model => new AiModelSettingsDto(
                    model.ModelId,
                    model.DisplayName,
                    CapabilityNames(model.Capabilities))).ToArray()));
        }

        return new AiSettingsDto(
            providerSettings,
            new AiChatRouteDto(
                settings.DefaultChatRoute.ProviderId,
                settings.DefaultChatRoute.ModelId),
            "Codex");
    }

    public async Task<AiSettingsDto> SetChatRouteAsync(
        SetChatRouteRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = new ChatModelRoute(
            Required(request.ProviderId, nameof(request.ProviderId)),
            Required(request.ModelId, nameof(request.ModelId)));
        EnsureRegisteredRoute(route);
        await settingsStore.SaveAsync(new AiSettings(route), cancellationToken)
            .ConfigureAwait(false);
        return await GetAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AiProviderCredentialStatusDto> SetProviderCredentialAsync(
        SetProviderCredentialRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = GetDescriptor(request.ProviderId);
        EnsureApiKeyProvider(descriptor);
        if (string.IsNullOrWhiteSpace(request.Secret)
            || request.Secret.Length > MaximumCredentialCharacters
            || !string.Equals(request.Secret, request.Secret.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "API Key 不能为空、不能带首尾空格，且长度不能超过 8192 个字符。",
                nameof(request));
        }

        await credentials.SetAsync(
                descriptor.ProviderId,
                request.Secret.AsMemory(),
                cancellationToken)
            .ConfigureAwait(false);
        _health.TryRemove(descriptor.ProviderId, out _);
        return new AiProviderCredentialStatusDto(
            descriptor.ProviderId,
            ProviderCredentialState.Configured.ToString(),
            "API Key 已加密保存；元枢不会在界面中读回它。");
    }

    public async Task<AiProviderCredentialStatusDto> DeleteProviderCredentialAsync(
        ProviderIdRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = GetDescriptor(request.ProviderId);
        EnsureApiKeyProvider(descriptor);
        _ = await credentials.DeleteAsync(descriptor.ProviderId, cancellationToken)
            .ConfigureAwait(false);
        _health.TryRemove(descriptor.ProviderId, out _);
        return new AiProviderCredentialStatusDto(
            descriptor.ProviderId,
            ProviderCredentialState.Missing.ToString(),
            $"API Key 已删除；{descriptor.DisplayName}普通聊天在重新配置前不可用。");
    }

    public async Task<AiProviderHealthDto> CheckProviderHealthAsync(
        ProviderIdRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = GetDescriptor(request.ProviderId);
        var credentialStatus = await GetCredentialStatusAsync(descriptor, cancellationToken)
            .ConfigureAwait(false);
        var isConfigured = descriptor.CredentialKind == ChatProviderCredentialKind.None
                           || credentialStatus.State == ProviderCredentialState.Configured;
        if (!isConfigured)
        {
            var notConfigured = new ChatProviderHealth(
                descriptor.ProviderId,
                ChatProviderHealthState.NotConfigured,
                IsConfigured: false,
                "尚未配置 API Key。",
                DateTimeOffset.UtcNow);
            _health[descriptor.ProviderId] = notConfigured;
            return MapHealth(notConfigured);
        }

        var provider = providers.GetProviderRequired(descriptor.ProviderId);
        ChatProviderHealth health;
        try
        {
            health = await provider.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            health = new ChatProviderHealth(
                descriptor.ProviderId,
                ChatProviderHealthState.Unavailable,
                IsConfigured: true,
                "连接检查没有成功，请稍后再试。",
                DateTimeOffset.UtcNow);
        }

        var safeHealth = health with
        {
            ProviderId = descriptor.ProviderId,
            IsConfigured = true,
            Message = SafeMessage(health.Message)
        };
        _health[descriptor.ProviderId] = safeHealth;
        return MapHealth(safeHealth);
    }

    private void EnsureRegisteredRoute(ChatModelRoute route)
    {
        try
        {
            _ = providers.GetRequired(route.ProviderId, route.ModelId);
        }
        catch (KeyNotFoundException)
        {
            throw new ChatModelException(
                route.ProviderId,
                route.ModelId,
                new ChatModelError(
                    ChatModelErrorKind.ModelNotFound,
                    "chat_route_not_found",
                    "所选普通聊天模型当前不可用，请重新选择。",
                    IsRetryable: false));
        }
    }

    private ChatProviderDescriptor GetDescriptor(string providerId)
    {
        var normalized = Required(providerId, nameof(providerId));
        return providers.Providers.SingleOrDefault(item =>
                   string.Equals(item.ProviderId, normalized, StringComparison.OrdinalIgnoreCase))
               ?? throw new KeyNotFoundException("没有找到这个 AI Provider。");
    }

    private static void EnsureApiKeyProvider(ChatProviderDescriptor descriptor)
    {
        if (descriptor.CredentialKind != ChatProviderCredentialKind.ApiKey)
        {
            throw new InvalidOperationException("这个 AI Provider 不接受由元枢保存的 API Key。");
        }
    }

    private async Task<ProviderCredentialStatus> GetCredentialStatusAsync(
        ChatProviderDescriptor descriptor,
        CancellationToken cancellationToken) =>
        descriptor.CredentialKind == ChatProviderCredentialKind.None
            ? new ProviderCredentialStatus(
                descriptor.ProviderId,
                ProviderCredentialState.Configured,
                null)
            : await credentials.GetStatusAsync(descriptor.ProviderId, cancellationToken)
                .ConfigureAwait(false);

    private ChatProviderHealth CurrentHealth(
        ChatProviderDescriptor descriptor,
        ProviderCredentialStatus credentialStatus)
    {
        if (descriptor.CredentialKind == ChatProviderCredentialKind.ApiKey
            && credentialStatus.State == ProviderCredentialState.Missing)
        {
            return new ChatProviderHealth(
                descriptor.ProviderId,
                ChatProviderHealthState.NotConfigured,
                IsConfigured: false,
                "尚未配置 API Key。",
                DateTimeOffset.MinValue);
        }

        return _health.TryGetValue(descriptor.ProviderId, out var health)
            ? health
            : new ChatProviderHealth(
                descriptor.ProviderId,
                ChatProviderHealthState.NotChecked,
                IsConfigured: true,
                "尚未检查连接。",
                DateTimeOffset.MinValue);
    }

    private static string ConfigurationState(
        ChatProviderDescriptor descriptor,
        ProviderCredentialStatus credentialStatus) =>
        descriptor.CredentialKind == ChatProviderCredentialKind.None
            ? "NotRequired"
            : credentialStatus.State.ToString();

    private static AiProviderHealthDto MapHealth(ChatProviderHealth health) => new(
        health.ProviderId,
        health.State == ChatProviderHealthState.PolicyDisabled
            ? ChatProviderHealthState.Unavailable.ToString()
            : health.State.ToString(),
        health.IsConfigured,
        SafeMessage(health.Message),
        health.CheckedAtUtc == DateTimeOffset.MinValue ? null : health.CheckedAtUtc);

    private static IReadOnlyList<string> CapabilityNames(ChatModelCapabilities capabilities)
    {
        var values = new List<string>();
        Add(ChatModelCapabilities.Streaming, nameof(ChatModelCapabilities.Streaming));
        Add(ChatModelCapabilities.ToolCalling, nameof(ChatModelCapabilities.ToolCalling));
        Add(ChatModelCapabilities.Vision, nameof(ChatModelCapabilities.Vision));
        Add(ChatModelCapabilities.JsonObjectOutput, nameof(ChatModelCapabilities.JsonObjectOutput));
        Add(ChatModelCapabilities.JsonSchemaOutput, nameof(ChatModelCapabilities.JsonSchemaOutput));
        Add(ChatModelCapabilities.Reasoning, nameof(ChatModelCapabilities.Reasoning));
        return values;

        void Add(ChatModelCapabilities flag, string name)
        {
            if (capabilities.HasFlag(flag))
            {
                values.Add(name);
            }
        }
    }

    private static string Required(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("值不能为空。", parameterName)
            : value.Trim();

    private static string SafeMessage(string? value)
    {
        var redacted = SensitiveDataSanitizer.Redact(value);
        if (string.IsNullOrWhiteSpace(redacted))
        {
            return "状态信息不可用。";
        }

        return redacted.Length <= 300 ? redacted : redacted[..300];
    }
}
