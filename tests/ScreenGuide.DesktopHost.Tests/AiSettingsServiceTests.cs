using ScreenGuide.AI.Core;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class AiSettingsServiceTests
{
    [Fact]
    public async Task Read_does_not_probe_network_and_discloses_destination_auth_and_role_boundary()
    {
        var codex = new SettingsProvider(
            "codex",
            "codex-default",
            ChatProviderCredentialKind.None,
            "OpenAI Codex cloud");
        var deepseek = new SettingsProvider(
            "deepseek",
            "deepseek-v4-flash",
            ChatProviderCredentialKind.ApiKey,
            "https://api.deepseek.com");
        var service = Service(codex, deepseek);

        var settings = await service.GetAsync();

        Assert.Equal(new AiChatRouteDto("codex", "codex-default"), settings.CurrentChatRoute);
        Assert.Equal("Codex", settings.ProgrammingAgent);
        Assert.Equal(0, codex.HealthChecks);
        Assert.Equal(0, deepseek.HealthChecks);
        Assert.Equal("NotRequired", settings.Providers.Single(item => item.ProviderId == "codex").ConfigurationState);
        var deepseekSettings = settings.Providers.Single(item => item.ProviderId == "deepseek");
        Assert.Equal("Missing", deepseekSettings.ConfigurationState);
        Assert.Equal("NotConfigured", deepseekSettings.Health.State);
        Assert.Equal("https://api.deepseek.com", deepseekSettings.DataDestination);
    }

    [Fact]
    public async Task Route_change_affects_settings_only_and_never_changes_programming_agent()
    {
        var codex = new SettingsProvider("codex", "codex-default", ChatProviderCredentialKind.None, "Codex");
        var deepseek = new SettingsProvider("deepseek", "deepseek-v4-pro", ChatProviderCredentialKind.ApiKey, "DeepSeek");
        var settingsStore = new MemorySettingsStore(new AiSettings(
            new ChatModelRoute("codex", "codex-default")));
        var service = Service(settingsStore, new MemoryCredentialStore(), codex, deepseek);

        var updated = await service.SetChatRouteAsync(
            new SetChatRouteRequestDto("deepseek", "deepseek-v4-pro"));

        Assert.Equal("deepseek", updated.CurrentChatRoute.ProviderId);
        Assert.Equal("deepseek-v4-pro", updated.CurrentChatRoute.ModelId);
        Assert.Equal("Codex", updated.ProgrammingAgent);
        Assert.Equal("deepseek", (await settingsStore.LoadAsync()).DefaultChatRoute.ProviderId);
    }

    [Fact]
    public async Task Credential_is_write_only_and_health_is_checked_only_on_explicit_request()
    {
        const string key = "sk-stage2-ui-must-never-return";
        var deepseek = new SettingsProvider(
            "deepseek",
            "deepseek-v4-flash",
            ChatProviderCredentialKind.ApiKey,
            "DeepSeek");
        var credentials = new MemoryCredentialStore();
        var service = Service(
            new MemorySettingsStore(new AiSettings(
                new ChatModelRoute("deepseek", "deepseek-v4-flash"))),
            credentials,
            deepseek);

        var credentialStatus = await service.SetProviderCredentialAsync(
            new SetProviderCredentialRequestDto("deepseek", key));
        var settings = await service.GetAsync();
        var health = await service.CheckProviderHealthAsync(
            new ProviderIdRequestDto("deepseek"));
        var deleted = await service.DeleteProviderCredentialAsync(
            new ProviderIdRequestDto("deepseek"));

        Assert.Equal("Configured", credentialStatus.ConfigurationState);
        Assert.Equal("Configured", settings.Providers.Single().ConfigurationState);
        Assert.Equal(1, deepseek.HealthChecks);
        Assert.Equal("Healthy", health.State);
        Assert.Equal("Missing", deleted.ConfigurationState);
        var exposed = string.Join('|',
            credentialStatus.ToString(),
            settings.ToString(),
            health.ToString(),
            deleted.ToString());
        Assert.DoesNotContain(key, exposed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_route_and_credential_for_no_key_provider_fail_closed()
    {
        var codex = new SettingsProvider(
            "codex",
            "codex-default",
            ChatProviderCredentialKind.None,
            "Codex");
        var service = Service(codex);

        var routeError = await Assert.ThrowsAsync<ChatModelException>(() =>
            service.SetChatRouteAsync(new SetChatRouteRequestDto("missing", "model")));
        var credentialError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetProviderCredentialAsync(
                new SetProviderCredentialRequestDto("codex", "must-not-store")));

        Assert.Equal(ChatModelErrorKind.ModelNotFound, routeError.Error.Kind);
        Assert.DoesNotContain("must-not-store", credentialError.Message, StringComparison.Ordinal);
    }

    private static AiSettingsService Service(params SettingsProvider[] providers) =>
        Service(
            new MemorySettingsStore(new AiSettings(
                new ChatModelRoute("codex", "codex-default"))),
            new MemoryCredentialStore(),
            providers);

    private static AiSettingsService Service(
        IAiSettingsStore settings,
        IProviderCredentialStore credentials,
        params SettingsProvider[] providers) =>
        new(new ChatProviderRegistry(providers), settings, credentials);

    private sealed class SettingsProvider(
        string providerId,
        string modelId,
        ChatProviderCredentialKind credentialKind,
        string destination) : IChatModelProvider
    {
        public ChatProviderDescriptor Descriptor { get; } = new(
            providerId,
            providerId,
            destination,
            true,
            [new ChatModelDescriptor(
                modelId,
                modelId,
                ChatModelCapabilities.Streaming | ChatModelCapabilities.JsonObjectOutput)],
            credentialKind);

        public int HealthChecks { get; private set; }

        public Task<ChatProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            HealthChecks++;
            return Task.FromResult(new ChatProviderHealth(
                providerId,
                ChatProviderHealthState.Healthy,
                IsConfigured: true,
                "连接正常。",
                DateTimeOffset.UtcNow));
        }

        public Task<ChatModelResponse> CompleteAsync(ChatModelRequest request, ChatModelStreamCallback? streamCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MemorySettingsStore(AiSettings settings) : IAiSettingsStore
    {
        private AiSettings _settings = settings;
        public Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_settings);
        public Task SaveAsync(AiSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryCredentialStore : IProviderCredentialStore
    {
        private readonly Dictionary<string, char[]> _secrets = new(StringComparer.OrdinalIgnoreCase);

        public Task<ProviderCredentialStatus> GetStatusAsync(string providerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderCredentialStatus(
                providerId,
                _secrets.ContainsKey(providerId)
                    ? ProviderCredentialState.Configured
                    : ProviderCredentialState.Missing,
                _secrets.ContainsKey(providerId) ? DateTimeOffset.UtcNow : null));

        public Task SetAsync(string providerId, ReadOnlyMemory<char> secret, CancellationToken cancellationToken = default)
        {
            _secrets[providerId] = secret.ToArray();
            return Task.CompletedTask;
        }

        public ValueTask<IProviderCredentialLease?> OpenLeaseAsync(string providerId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IProviderCredentialLease?>(null);

        public Task<bool> DeleteAsync(string providerId, CancellationToken cancellationToken = default)
        {
            if (!_secrets.Remove(providerId, out var secret))
            {
                return Task.FromResult(false);
            }

            secret.AsSpan().Clear();
            return Task.FromResult(true);
        }
    }
}
