namespace ScreenGuide.DesktopProtocol;

public sealed record ProviderIdRequestDto(string ProviderId);

public sealed record SetChatRouteRequestDto(
    string ProviderId,
    string ModelId);

public sealed record SetProviderCredentialRequestDto(
    string ProviderId,
    string Secret)
{
    public override string ToString() =>
        $"{nameof(SetProviderCredentialRequestDto)} {{ ProviderId = {SensitiveDataSanitizer.Redact(ProviderId)}, Secret = [REDACTED] }}";
}

public sealed record AiChatRouteDto(
    string ProviderId,
    string ModelId);

public sealed record AiModelSettingsDto(
    string ModelId,
    string DisplayName,
    IReadOnlyList<string> Capabilities);

public sealed record AiProviderHealthDto(
    string ProviderId,
    string State,
    bool IsConfigured,
    string SafeMessage,
    DateTimeOffset? CheckedAtUtc = null);

public sealed record AiProviderSettingsDto(
    string ProviderId,
    string DisplayName,
    string DataDestination,
    bool SendsDataOffDevice,
    string ConfigurationState,
    AiProviderHealthDto Health,
    IReadOnlyList<AiModelSettingsDto> Models);

public sealed record AiProviderCredentialStatusDto(
    string ProviderId,
    string ConfigurationState,
    string SafeMessage);

public sealed record AiSettingsDto(
    IReadOnlyList<AiProviderSettingsDto> Providers,
    AiChatRouteDto CurrentChatRoute,
    string ProgrammingAgent);
