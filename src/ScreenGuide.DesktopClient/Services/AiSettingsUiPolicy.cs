using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Services;

public sealed record AiCredentialPresentation(
    bool ShowCredentialInputs,
    bool CanSave,
    bool CanDelete,
    string StatusText);

public static class AiSettingsUiPolicy
{
    public static string? SelectModelId(
        AiProviderSettingsDto provider,
        string? requestedModelId,
        AiChatRouteDto? currentRoute)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.Models.Any(model => string.Equals(
                model.ModelId,
                requestedModelId,
                StringComparison.OrdinalIgnoreCase)))
        {
            return requestedModelId;
        }

        if (currentRoute is not null
            && string.Equals(
                provider.ProviderId,
                currentRoute.ProviderId,
                StringComparison.OrdinalIgnoreCase)
            && provider.Models.Any(model => string.Equals(
                model.ModelId,
                currentRoute.ModelId,
                StringComparison.OrdinalIgnoreCase)))
        {
            return currentRoute.ModelId;
        }

        return provider.Models.FirstOrDefault()?.ModelId;
    }

    public static AiCredentialPresentation PresentCredential(
        AiProviderSettingsDto provider,
        bool hasSecretInput,
        bool hostOnline)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var notRequired = string.Equals(
            provider.ConfigurationState,
            "NotRequired",
            StringComparison.OrdinalIgnoreCase);
        if (notRequired)
        {
            return new AiCredentialPresentation(
                ShowCredentialInputs: false,
                CanSave: false,
                CanDelete: false,
                "使用 Codex 登录账号，无需在元枢保存 Key。");
        }

        var configured = string.Equals(
            provider.ConfigurationState,
            "Configured",
            StringComparison.OrdinalIgnoreCase);
        return new AiCredentialPresentation(
            ShowCredentialInputs: true,
            CanSave: hostOnline && hasSecretInput,
            CanDelete: hostOnline && configured,
            configured
                ? "状态：已配置（密钥不会显示或读回）"
                : "状态：未配置");
    }

    public static AiProviderSettingsDto ApplyHealth(
        AiProviderSettingsDto provider,
        AiProviderHealthDto health)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(health);
        var configurationState = string.Equals(
            provider.ConfigurationState,
            "NotRequired",
            StringComparison.OrdinalIgnoreCase)
            ? "NotRequired"
            : health.IsConfigured ? "Configured" : "Missing";
        return provider with
        {
            ConfigurationState = configurationState,
            Health = health
        };
    }

    public static string CredentialDeleteConfirmation(AiProviderSettingsDto provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var displayName = SensitiveDataSanitizer.Redact(provider.DisplayName);
        return $"确定删除“{displayName}”的密钥吗？删除后，重新保存密钥前将无法使用普通聊天。";
    }
}

public static class AiCredentialSubmission
{
    public static async Task RunAsync(
        string secret,
        Func<string, Task> submit,
        Action clearInput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentNullException.ThrowIfNull(submit);
        ArgumentNullException.ThrowIfNull(clearInput);
        try
        {
            await submit(secret).ConfigureAwait(true);
        }
        finally
        {
            clearInput();
        }
    }
}
