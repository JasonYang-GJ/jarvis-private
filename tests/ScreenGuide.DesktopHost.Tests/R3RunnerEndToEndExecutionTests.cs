using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.AI.Core;
using ScreenGuide.AI.DeepSeek;
using ScreenGuide.Core.Ai;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Sessions;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.DesktopV01.RealAcceptanceRunner;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class R3RunnerEndToEndExecutionTests
{
    [Fact]
    public async Task SharedRunnerExecutionPersistsRealCancelledSessionConversationAndInvocationWithoutPersistingCredential()
    {
        const string sentinel = "fake-r3-end-to-end-credential-sentinel";
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ScreenGuide.R3.RunnerExecution.Tests.{Guid.NewGuid():N}");
        var arguments = CancellationOnlyArguments();
        var validation = R3DeepSeekValidationOptions.Parse(arguments);
        var hostOptions = new DesktopHostOptions(
            Path.Combine(root, "isolated-data"),
            pipeName: $"ScreenGuide.R3.Execution.{Guid.NewGuid():N}");
        var credentials = new FakeCredentialStore(FakeCredentialMode.Valid, sentinel);
        var transport = new CancellationSseTransport();
        try
        {
            _ = await R3IsolatedAiSettingsMaterializer.MaterializeAndVerifyAsync(
                hostOptions.AiSettingsPath,
                DeepSeekChatModelProvider.ProModelId);
            using var host = R3RunnerHostComposition.BuildOffline(
                hostOptions,
                validation,
                credentials,
                transport);
            var hostStopped = false;
            await host.StartAsync();
            try
            {
                var api = new DesktopApiClient(hostOptions.PipeName, TimeSpan.FromSeconds(5));
                Assert.True(await api.PingAsync());
                var provider = host.Services.GetRequiredService<R3BudgetedChatModelProvider>();
                var tracker = new R3RunEvidenceTracker(DeepSeekChatModelProvider.ProModelId);

                var outcome = await R3RunnerExecution.ExecuteCancellationOnlyAsync(
                    api,
                    host,
                    provider,
                    validation,
                    tracker);

                Assert.True(outcome.Passed);
                Assert.NotNull(outcome.Success);
                Assert.Null(outcome.Failure);
                Assert.True(outcome.Success!.CancellationPassed);
                Assert.True(outcome.Success.AuditPassed);
                Assert.True(outcome.Success.CancellationTerminalEvidence?.IsCancelled);
                Assert.Equal(1, outcome.Success.Requests);
                Assert.Equal(1, transport.SendCount);
                Assert.True(transport.CancellationObserved);
                var snapshot = await api.GetCurrentSessionAsync();
                var sessionTurn = Assert.Single(snapshot!.Turns);
                Assert.Equal("Cancelled", sessionTurn.Phase);
                var conversationTurn = Assert.Single(await host.Services
                    .GetRequiredService<IConversationStore>()
                    .GetTurnsAsync(snapshot.ConversationId));
                Assert.Equal(ConversationTurnStatus.Cancelled, conversationTurn.Status);
                var invocation = Assert.Single(await host.Services
                    .GetRequiredService<IAiInvocationStore>()
                    .GetForSessionTurnAsync(sessionTurn.Id));
                Assert.Equal(AiInvocationStatus.Cancelled, invocation.Status);
                Assert.Equal("deepseek.cancelled", invocation.FailureCode);
                Assert.Null(invocation.Usage);
                Assert.Null(invocation.ProviderRequestId);
                Assert.Equal(1, credentials.OpenLeaseCount);
                Assert.True(credentials.LastLeaseDisposed);
                await host.StopAsync();
                hostStopped = true;
                Assert.DoesNotContain(sentinel, outcome.StructuredEvidenceJson, StringComparison.Ordinal);
                Assert.DoesNotContain(arguments, argument =>
                    argument.Contains(sentinel, StringComparison.Ordinal));
                Assert.All(
                    Directory.EnumerateFiles(
                        hostOptions.DataDirectory,
                        "*",
                        SearchOption.AllDirectories),
                    path => Assert.False(File.ReadAllBytes(path).AsSpan().IndexOf(
                        Encoding.UTF8.GetBytes(sentinel)) >= 0));
                Assert.False(Directory.Exists(hostOptions.SecretsDirectory));
            }
            finally
            {
                if (!hostStopped)
                {
                    await host.StopAsync();
                }
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

    [Theory]
    [InlineData(FakeCredentialMode.Missing, false)]
    [InlineData(FakeCredentialMode.Throwing, false)]
    [InlineData(FakeCredentialMode.Empty, true)]
    [InlineData(FakeCredentialMode.Expired, true)]
    public async Task SharedRunnerExecutionFailsClosedWithStableAuditBeforeHttp(
        FakeCredentialMode credentialMode,
        bool leaseMustBeDisposed)
    {
        const string sentinel = "fake-r3-failing-credential-sentinel";
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ScreenGuide.R3.RunnerFailure.Tests.{Guid.NewGuid():N}");
        var arguments = CancellationOnlyArguments();
        var validation = R3DeepSeekValidationOptions.Parse(arguments);
        var hostOptions = new DesktopHostOptions(
            Path.Combine(root, "isolated-data"),
            pipeName: $"ScreenGuide.R3.Failure.{Guid.NewGuid():N}");
        var credentials = new FakeCredentialStore(credentialMode, sentinel);
        var transport = new NoSendTransport();
        try
        {
            _ = await R3IsolatedAiSettingsMaterializer.MaterializeAndVerifyAsync(
                hostOptions.AiSettingsPath,
                DeepSeekChatModelProvider.ProModelId);
            using var host = R3RunnerHostComposition.BuildOffline(
                hostOptions,
                validation,
                credentials,
                transport);
            var hostStopped = false;
            await host.StartAsync();
            try
            {
                var api = new DesktopApiClient(hostOptions.PipeName, TimeSpan.FromSeconds(5));
                Assert.True(await api.PingAsync());
                var provider = host.Services.GetRequiredService<R3BudgetedChatModelProvider>();
                var tracker = new R3RunEvidenceTracker(DeepSeekChatModelProvider.ProModelId);

                var outcome = await R3RunnerExecution.ExecuteCancellationOnlyAsync(
                    api,
                    host,
                    provider,
                    validation,
                    tracker);

                Assert.False(outcome.Passed);
                Assert.Null(outcome.Success);
                Assert.NotNull(outcome.Failure);
                Assert.Equal("configuration", outcome.Failure!.ErrorCode);
                Assert.Equal("deepseek.not_configured", outcome.Failure.ProviderDiagnosticCode);
                Assert.Equal(1, outcome.Failure.RequestCount);
                Assert.Equal(1, credentials.OpenLeaseCount);
                Assert.Equal(leaseMustBeDisposed, credentials.LastLeaseDisposed);
                Assert.Equal(0, transport.SendCount);
                Assert.DoesNotContain(sentinel, outcome.StructuredEvidenceJson, StringComparison.Ordinal);
                Assert.DoesNotContain(arguments, argument =>
                    argument.Contains(sentinel, StringComparison.Ordinal));
                var snapshot = await api.GetCurrentSessionAsync();
                var sessionTurn = Assert.Single(snapshot!.Turns);
                Assert.Equal("Failed", sessionTurn.Phase);
                var persistedSessionTurn = await host.Services
                    .GetRequiredService<ISessionStore>()
                    .GetTurnAsync(sessionTurn.Id);
                Assert.Equal("configuration", persistedSessionTurn!.FailureCode);
                var conversationTurn = Assert.Single(await host.Services
                    .GetRequiredService<IConversationStore>()
                    .GetTurnsAsync(snapshot.ConversationId));
                Assert.Equal(ConversationTurnStatus.Failed, conversationTurn.Status);
                Assert.Equal("configuration", conversationTurn.FailureCode);
                var invocation = Assert.Single(await host.Services
                    .GetRequiredService<IAiInvocationStore>()
                    .GetForSessionTurnAsync(sessionTurn.Id));
                Assert.Equal(AiInvocationStatus.Failed, invocation.Status);
                Assert.Equal("deepseek.not_configured", invocation.FailureCode);
                Assert.Null(invocation.Usage);
                Assert.Null(invocation.ProviderRequestId);
                await host.StopAsync();
                hostStopped = true;
                Assert.All(
                    Directory.EnumerateFiles(
                        hostOptions.DataDirectory,
                        "*",
                        SearchOption.AllDirectories),
                    path => Assert.False(File.ReadAllBytes(path).AsSpan().IndexOf(
                        Encoding.UTF8.GetBytes(sentinel)) >= 0));
                Assert.False(Directory.Exists(hostOptions.SecretsDirectory));
            }
            finally
            {
                if (!hostStopped)
                {
                    await host.StopAsync();
                }
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

    private static string[] CancellationOnlyArguments() =>
    [
        "--stage2-r3-deepseek",
        "--real-provider",
        "--cancellation-only",
        "--expected-model=deepseek-v4-pro",
        "--max-requests=1",
        "--max-output-tokens=64",
        "--total-timeout-seconds=30",
        "--no-automatic-retry",
        "--no-fallback"
    ];

    public enum FakeCredentialMode
    {
        Valid,
        Missing,
        Throwing,
        Empty,
        Expired
    }

    private sealed class FakeCredentialStore(FakeCredentialMode mode, string sentinel)
        : IProviderCredentialStore
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
            return ValueTask.FromResult<IProviderCredentialLease?>(mode switch
            {
                FakeCredentialMode.Valid => new FakeLease(
                    sentinel,
                    () => LastLeaseDisposed = true),
                FakeCredentialMode.Missing => null,
                FakeCredentialMode.Throwing => throw new InvalidOperationException(sentinel),
                FakeCredentialMode.Empty => new FakeLease(
                    string.Empty,
                    () => LastLeaseDisposed = true),
                FakeCredentialMode.Expired => new ExpiredLease(
                    sentinel,
                    () => LastLeaseDisposed = true),
                _ => throw new InvalidOperationException("Unsupported fake credential mode.")
            });
        }

        public Task<bool> DeleteAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Offline credential source is read-only.");

        private sealed class FakeLease(string secret, Action disposed)
            : IProviderCredentialLease
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

        private sealed class ExpiredLease(string sentinel, Action disposed)
            : IProviderCredentialLease
        {
            public ReadOnlyMemory<char> Secret =>
                throw new ObjectDisposedException("expired-lease", sentinel);

            public void Dispose() => disposed();
        }
    }

    private sealed class NoSendTransport : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            throw new Xunit.Sdk.XunitException("Credential failure must occur before HTTP.");
        }
    }

    private sealed class CancellationSseTransport : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        public bool CancellationObserved => _stream?.CancellationObserved ?? false;

        private BlockingSseStream? _stream;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            _stream = new BlockingSseStream(
                """
                data: {"id":"offline-cancel","choices":[{"delta":{"content":"one"},"finish_reason":null}]}

                data: {"id":"offline-cancel","choices":[{"delta":{"content":"two"},"finish_reason":null}]}

                """);
            var content = new StreamContent(_stream);
            content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
        }
    }

    private sealed class BlockingSseStream(string firstChunk) : Stream
    {
        private readonly byte[] _firstChunk = Encoding.UTF8.GetBytes(firstChunk);
        private bool _sent;

        public bool CancellationObserved { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                _firstChunk.CopyTo(buffer);
                return _firstChunk.Length;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
