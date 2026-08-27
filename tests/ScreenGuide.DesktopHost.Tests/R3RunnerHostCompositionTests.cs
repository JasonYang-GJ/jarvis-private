using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.AI.Core;
using ScreenGuide.AI.DeepSeek;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopV01.RealAcceptanceRunner;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class R3RunnerHostCompositionTests
{
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
