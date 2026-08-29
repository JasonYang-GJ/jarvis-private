namespace ScreenGuide.Core.Memories;

public interface IMemoryContentProtector
{
    byte[] Protect(string plaintext);

    string Unprotect(ReadOnlySpan<byte> protectedContent);
}

public interface IMemoryStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProtectedMemoryItem>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<ProtectedMemoryItem?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task CreateAsync(
        ProtectedMemoryItem item,
        CancellationToken cancellationToken = default);

    Task<MemoryStoreMutationResult> UpdateAsync(
        ProtectedMemoryItem item,
        int expectedVersion,
        CancellationToken cancellationToken = default);

    Task<MemoryStoreMutationResult> SetEnabledAsync(
        Guid id,
        bool enabled,
        int expectedVersion,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);

    Task<MemoryStoreMutationResult> DeleteAsync(
        Guid id,
        int expectedVersion,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);
}

public enum MemoryStoreMutationDisposition
{
    Applied,
    NotFound,
    Conflict,
    AlreadyDeleted
}

public sealed record MemoryStoreMutationResult(
    MemoryStoreMutationDisposition Disposition,
    ProtectedMemoryItem? Current);
