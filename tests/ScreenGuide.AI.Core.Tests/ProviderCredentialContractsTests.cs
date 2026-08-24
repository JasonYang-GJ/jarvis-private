using ScreenGuide.AI.Core;
using Xunit;

namespace ScreenGuide.AI.Core.Tests;

public sealed class ProviderCredentialContractsTests
{
    [Fact]
    public async Task StoreContractSeparatesStatusWriteShortLivedReadAndDelete()
    {
        IProviderCredentialStore store = new VolatileCredentialStore();

        var missing = await store.GetStatusAsync("provider-a");
        await store.SetAsync("provider-a", "test-secret".AsMemory());
        var configured = await store.GetStatusAsync("provider-a");
        using var lease = await store.OpenLeaseAsync("provider-a");
        var removed = await store.DeleteAsync("provider-a");

        Assert.Equal(ProviderCredentialState.Missing, missing.State);
        Assert.Equal(ProviderCredentialState.Configured, configured.State);
        Assert.NotNull(configured.UpdatedAtUtc);
        Assert.True(lease!.Secret.Span.SequenceEqual("test-secret"));
        Assert.True(removed);
        Assert.Equal(ProviderCredentialState.Missing,
            (await store.GetStatusAsync("provider-a")).State);
    }

    private sealed class VolatileCredentialStore : IProviderCredentialStore
    {
        private char[]? _secret;
        private DateTimeOffset? _updatedAtUtc;

        public Task<ProviderCredentialStatus> GetStatusAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderCredentialStatus(
                providerId,
                _secret is null
                    ? ProviderCredentialState.Missing
                    : ProviderCredentialState.Configured,
                _updatedAtUtc));

        public Task SetAsync(
            string providerId,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default)
        {
            _secret = secret.ToArray();
            _updatedAtUtc = DateTimeOffset.Parse("2026-08-24T00:00:00Z");
            return Task.CompletedTask;
        }

        public ValueTask<IProviderCredentialLease?> OpenLeaseAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IProviderCredentialLease?>(
                _secret is null ? null : new Lease(_secret.ToArray()));

        public Task<bool> DeleteAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            var removed = _secret is not null;
            if (_secret is not null)
            {
                Array.Clear(_secret);
            }

            _secret = null;
            _updatedAtUtc = null;
            return Task.FromResult(removed);
        }

        private sealed class Lease(char[] secret) : IProviderCredentialLease
        {
            private char[]? _secret = secret;

            public ReadOnlyMemory<char> Secret => _secret
                ?? throw new ObjectDisposedException(nameof(Lease));

            public void Dispose()
            {
                if (_secret is not null)
                {
                    Array.Clear(_secret);
                    _secret = null;
                }
            }
        }
    }
}
