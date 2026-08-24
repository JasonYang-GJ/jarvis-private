namespace ScreenGuide.AI.Core;

public enum ProviderCredentialState
{
    Missing,
    Configured
}

public sealed record ProviderCredentialStatus(
    string ProviderId,
    ProviderCredentialState State,
    DateTimeOffset? UpdatedAtUtc);

public interface IProviderCredentialLease : IDisposable
{
    ReadOnlyMemory<char> Secret { get; }
}

public interface IProviderCredentialStore
{
    Task<ProviderCredentialStatus> GetStatusAsync(
        string providerId,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        string providerId,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default);

    ValueTask<IProviderCredentialLease?> OpenLeaseAsync(
        string providerId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string providerId,
        CancellationToken cancellationToken = default);
}
