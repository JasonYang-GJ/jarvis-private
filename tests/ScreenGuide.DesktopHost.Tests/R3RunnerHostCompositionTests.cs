using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.AI.Core;
using ScreenGuide.AI.DeepSeek;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.DesktopV01.RealAcceptanceRunner;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class R3RunnerHostCompositionTests
{
    [Fact]
    public async Task ActualRunnerHostCompositionAllowsVisibleCredentialSaveRefreshAndDelete()
    {
        const string sentinel = "fake-r3-visible-save-sentinel";
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ScreenGuide.R3.RunnerCredentialSave.Tests.{Guid.NewGuid():N}");
        var hostOptions = new DesktopHostOptions(
            Path.Combine(root, "isolated-data"),
            pipeName: $"ScreenGuide.R3.CredentialSave.{Guid.NewGuid():N}");
        var validation = R3DeepSeekValidationOptions.Parse(
        [
            "--stage2-r3-deepseek",
            "--real-provider",
            "--expected-model=deepseek-v4-pro",
            "--max-requests=1",
            "--max-output-tokens=64",
            "--total-timeout-seconds=30",
            "--no-automatic-retry",
            "--no-fallback"
        ]);
        var credentialStore = new WritableFakeCredentialStore();
        var transport = new StubTransport();

        try
        {
            _ = await R3IsolatedAiSettingsMaterializer.MaterializeAndVerifyAsync(
                hostOptions.AiSettingsPath,
                DeepSeekChatModelProvider.ProModelId);
            using var host = R3RunnerHostComposition.BuildOffline(
                hostOptions,
                validation,
                credentialStore,
                transport);
            await host.StartAsync();
            IDesktopApiClient client = new DesktopApiClient(
                hostOptions.PipeName,
                TimeSpan.FromSeconds(3));

            try
            {
                var saved = await client.SetProviderCredentialAsync(
                    new SetProviderCredentialRequestDto("deepseek", sentinel));
                var refreshed = await client.GetAiSettingsAsync();
                var deleted = await client.DeleteProviderCredentialAsync(
                    new ProviderIdRequestDto("deepseek"));
                var afterDelete = await client.GetAiSettingsAsync();

                Assert.Equal("Configured", saved.ConfigurationState);
                Assert.Equal(
                    "Configured",
                    refreshed.Providers.Single(item => item.ProviderId == "deepseek")
                        .ConfigurationState);
                Assert.Equal(
                    new AiChatRouteDto("deepseek", DeepSeekChatModelProvider.ProModelId),
                    refreshed.CurrentChatRoute);
                Assert.Equal("Codex", refreshed.ProgrammingAgent);
                Assert.Equal("Missing", deleted.ConfigurationState);
                Assert.Equal(
                    "Missing",
                    afterDelete.Providers.Single(item => item.ProviderId == "deepseek")
                        .ConfigurationState);
                Assert.Equal(1, credentialStore.SetCount);
                Assert.Equal(1, credentialStore.DeleteCount);
                Assert.False(credentialStore.HasSecret);
                Assert.Equal(0, transport.SendCount);
                Assert.DoesNotContain(sentinel, saved.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain(sentinel, refreshed.ToString(), StringComparison.Ordinal);
            }
            finally
            {
                await host.StopAsync();
            }

            var sentinelBytes = Encoding.UTF8.GetBytes(sentinel);
            Assert.All(
                Directory.EnumerateFiles(hostOptions.DataDirectory, "*", SearchOption.AllDirectories),
                path => Assert.True(File.ReadAllBytes(path).AsSpan().IndexOf(sentinelBytes) < 0));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("missing_input", "input_invalid")]
    [InlineData("save_failure", "ai_credential_write_failed")]
    [InlineData("status_failure", "ai_credential_status_failed")]
    [InlineData("delete_failure", "ai_credential_delete_failed")]
    public async Task ActualRunnerHostCompositionReturnsStableCredentialErrors(
        string failureMode,
        string expectedCode)
    {
        const string sentinel = "fake-r3-error-sentinel";
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ScreenGuide.R3.RunnerCredentialErrors.Tests.{Guid.NewGuid():N}");
        var hostOptions = new DesktopHostOptions(
            Path.Combine(root, "isolated-data"),
            pipeName: $"ScreenGuide.R3.CredentialErrors.{Guid.NewGuid():N}");
        var validation = R3DeepSeekValidationOptions.Parse(
        [
            "--stage2-r3-deepseek",
            "--real-provider",
            "--expected-model=deepseek-v4-pro",
            "--max-requests=1",
            "--max-output-tokens=64",
            "--total-timeout-seconds=30",
            "--no-automatic-retry",
            "--no-fallback"
        ]);
        var transport = new StubTransport();

        try
        {
            _ = await R3IsolatedAiSettingsMaterializer.MaterializeAndVerifyAsync(
                hostOptions.AiSettingsPath,
                DeepSeekChatModelProvider.ProModelId);
            using var host = R3RunnerHostComposition.BuildOffline(
                hostOptions,
                validation,
                new FailingFakeCredentialStore(failureMode),
                transport);
            await host.StartAsync();
            IDesktopApiClient client = new DesktopApiClient(
                hostOptions.PipeName,
                TimeSpan.FromSeconds(3));

            try
            {
                var exception = await Assert.ThrowsAsync<DesktopApiException>(async () =>
                {
                    switch (failureMode)
                    {
                        case "missing_input":
                            _ = await client.SetProviderCredentialAsync(
                                new SetProviderCredentialRequestDto("deepseek", " "));
                            break;
                        case "save_failure":
                            _ = await client.SetProviderCredentialAsync(
                                new SetProviderCredentialRequestDto("deepseek", sentinel));
                            break;
                        case "status_failure":
                            _ = await client.GetAiSettingsAsync();
                            break;
                        case "delete_failure":
                            _ = await client.DeleteProviderCredentialAsync(
                                new ProviderIdRequestDto("deepseek"));
                            break;
                        default:
                            throw new InvalidOperationException("Unknown test failure mode.");
                    }
                });

                Assert.Equal(expectedCode, exception.Error.Code);
                Assert.NotEqual("host_error", exception.Error.Code);
                Assert.DoesNotContain(
                    sentinel,
                    JsonSerializer.Serialize(exception.Error),
                    StringComparison.Ordinal);
                Assert.Equal(0, transport.SendCount);
            }
            finally
            {
                await host.StopAsync();
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ActualRunnerHostCompositionUsesFakeSecureLeaseWithoutPersistingIt()
    {
        const string sentinel = "fake-r3-host-composition-sentinel";
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ScreenGuide.R3.RunnerHostComposition.Tests.{Guid.NewGuid():N}");
        var hostOptions = new DesktopHostOptions(
            Path.Combine(root, "isolated-data"),
            pipeName: $"ScreenGuide.R3.Offline.{Guid.NewGuid():N}");
        var arguments = new[]
        {
            "--stage2-r3-deepseek",
            "--real-provider",
            "--expected-model=deepseek-v4-pro",
            "--max-requests=1",
            "--max-output-tokens=64",
            "--total-timeout-seconds=30",
            "--no-automatic-retry",
            "--no-fallback"
        };
        var validation = R3DeepSeekValidationOptions.Parse(arguments);
        var credentialStore = new FakeCredentialStore(sentinel);
        var transport = new StubTransport();
        try
        {
            _ = await R3IsolatedAiSettingsMaterializer.MaterializeAndVerifyAsync(
                hostOptions.AiSettingsPath,
                DeepSeekChatModelProvider.ProModelId);
            using var host = R3RunnerHostComposition.BuildOffline(
                hostOptions,
                validation,
                credentialStore,
                transport);
            await host.StartAsync();
            try
            {
                var provider = host.Services.GetRequiredService<R3BudgetedChatModelProvider>();
                var response = await provider.CompleteAsync(new ChatModelRequest(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    DeepSeekChatModelProvider.ProModelId,
                    "offline-system",
                    [new ChatMessage(ChatMessageRole.User, "offline-input")],
                    new ChatModelOptions(MaxOutputTokens: 64),
                    ChatResponseFormat.Text,
                    new ChatPromptReference("r3.offline", "1", new string('A', 64))));

                Assert.Equal("offline-ok", response.Text);
                Assert.Equal(1, provider.RequestCount);
            }
            finally
            {
                await host.StopAsync();
            }

            var settings = new FileAiSettingsStore(
                hostOptions.AiSettingsPath,
                new AiSettings(new ChatModelRoute("codex", "codex-default")));
            using (settings)
            {
                var loaded = await settings.LoadAsync();
                Assert.Equal("deepseek", loaded.DefaultChatRoute.ProviderId);
                Assert.Equal(
                    DeepSeekChatModelProvider.ProModelId,
                    loaded.DefaultChatRoute.ModelId);
            }

            var safeResult = JsonSerializer.Serialize(new
            {
                Passed = true,
                credentialStore.OpenLeaseCount,
                credentialStore.LastLeaseDisposed,
                transport.SendCount
            });
            var persistedFiles = Directory.EnumerateFiles(
                    hostOptions.DataDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .ToArray();

            Assert.True(File.Exists(hostOptions.DatabasePath));
            Assert.Equal(1, credentialStore.OpenLeaseCount);
            Assert.True(credentialStore.LastLeaseDisposed);
            Assert.Equal(1, transport.SendCount);
            Assert.DoesNotContain(sentinel, safeResult, StringComparison.Ordinal);
            Assert.DoesNotContain(arguments, argument =>
                argument.Contains(sentinel, StringComparison.Ordinal));
            Assert.All(persistedFiles, path =>
                Assert.False(File.ReadAllBytes(path).AsSpan().IndexOf(
                    Encoding.UTF8.GetBytes(sentinel)) >= 0));
            Assert.False(Directory.Exists(hostOptions.SecretsDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class FakeCredentialStore(string secret) : IProviderCredentialStore
    {
        public int OpenLeaseCount { get; private set; }

        public bool LastLeaseDisposed { get; private set; }

        public Task<ProviderCredentialStatus> GetStatusAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderCredentialStatus(
                providerId,
                ProviderCredentialState.Configured,
                DateTimeOffset.UnixEpoch));

        public Task SetAsync(
            string providerId,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Offline credential source is read-only.");

        public ValueTask<IProviderCredentialLease?> OpenLeaseAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            OpenLeaseCount++;
            return ValueTask.FromResult<IProviderCredentialLease?>(
                new FakeLease(secret, () => LastLeaseDisposed = true));
        }

        public Task<bool> DeleteAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Offline credential source is read-only.");

        private sealed class FakeLease(string secret, Action disposed) : IProviderCredentialLease
        {
            private char[]? _secret = secret.ToCharArray();

            public ReadOnlyMemory<char> Secret => _secret
                ?? throw new ObjectDisposedException(nameof(FakeLease));

            public void Dispose()
            {
                var value = Interlocked.Exchange(ref _secret, null);
                if (value is not null)
                {
                    value.AsSpan().Clear();
                    disposed();
                }
            }
        }
    }

    private sealed class WritableFakeCredentialStore : IProviderCredentialStore
    {
        private char[]? _secret;

        public int SetCount { get; private set; }

        public int DeleteCount { get; private set; }

        public bool HasSecret => _secret is not null;

        public Task<ProviderCredentialStatus> GetStatusAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderCredentialStatus(
                providerId,
                HasSecret ? ProviderCredentialState.Configured : ProviderCredentialState.Missing,
                HasSecret ? DateTimeOffset.UnixEpoch : null));

        public Task SetAsync(
            string providerId,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearSecret();
            _secret = secret.ToArray();
            SetCount++;
            return Task.CompletedTask;
        }

        public ValueTask<IProviderCredentialLease?> OpenLeaseAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IProviderCredentialLease?>(null);

        public Task<bool> DeleteAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existed = HasSecret;
            ClearSecret();
            DeleteCount++;
            return Task.FromResult(existed);
        }

        private void ClearSecret()
        {
            if (_secret is not null)
            {
                _secret.AsSpan().Clear();
                _secret = null;
            }
        }
    }

    private sealed class FailingFakeCredentialStore(string failureMode)
        : IProviderCredentialStore
    {
        public Task<ProviderCredentialStatus> GetStatusAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            failureMode == "status_failure"
                ? throw new ProviderCredentialStoreException(
                    "credential_status_failed",
                    "Fake status failure.")
                : Task.FromResult(new ProviderCredentialStatus(
                    providerId,
                    ProviderCredentialState.Missing,
                    null));

        public Task SetAsync(
            string providerId,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default) =>
            failureMode == "save_failure"
                ? throw new ProviderCredentialStoreException(
                    "credential_write_failed",
                    "Fake write failure.")
                : Task.CompletedTask;

        public ValueTask<IProviderCredentialLease?> OpenLeaseAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IProviderCredentialLease?>(null);

        public Task<bool> DeleteAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            failureMode == "delete_failure"
                ? throw new ProviderCredentialStoreException(
                    "credential_delete_failed",
                    "Fake delete failure.")
                : Task.FromResult(false);
    }

    private sealed class StubTransport : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "id": "offline-r3",
                      "choices": [
                        {
                          "message": { "role": "assistant", "content": "offline-ok" },
                          "finish_reason": "stop"
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
