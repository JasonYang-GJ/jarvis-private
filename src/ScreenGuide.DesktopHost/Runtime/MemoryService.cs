using System.Security.Cryptography;
using System.Text;
using ScreenGuide.Core.Memories;
using ScreenGuide.Core.Tasking;

namespace ScreenGuide.DesktopHost.Runtime;

public static class MemoryServiceErrorCodes
{
    public const string InvalidRequest = "memory.invalid_request";
    public const string NotFound = "memory.not_found";
    public const string Conflict = "memory.conflict";
    public const string StorageFailure = "memory.storage_failure";
    public const string ProtectionFailure = "memory.protection_failure";
}

public sealed class MemoryServiceException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = string.IsNullOrWhiteSpace(code)
        ? throw new ArgumentException("错误代码不能为空。", nameof(code))
        : code;
}

public sealed class MemoryService(
    IMemoryStore store,
    IMemoryContentProtector protector,
    ILocalTaskStore taskStore,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<MemoryItem>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var protectedItems = await store.ListAsync(cancellationToken).ConfigureAwait(false);
            return protectedItems.Select(Unprotect).ToArray();
        }
        catch (Exception exception) when (IsProtectionFailure(exception))
        {
            throw ProtectionFailure(exception);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            throw StorageFailure(exception);
        }
    }

    public async Task<MemoryItem> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw InvalidRequest();
        }

        try
        {
            var item = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (item is null || item.Metadata.Status == MemoryStatus.Deleted)
            {
                throw NotFound();
            }

            return Unprotect(item);
        }
        catch (MemoryServiceException)
        {
            throw;
        }
        catch (Exception exception) when (IsProtectionFailure(exception))
        {
            throw ProtectionFailure(exception);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            throw StorageFailure(exception);
        }
    }

    public async Task<MemoryItem> CreateAsync(
        MemoryDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await ValidateProjectScopeAsync(draft.Scope, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var metadata = MemoryMetadata.Create(
            Guid.NewGuid(),
            draft.Category,
            draft.Scope,
            MemoryStatus.Active,
            MemorySourceKind.UserExplicit,
            now,
            now,
            draft.ExpiresAtUtc,
            1.0,
            1);
        var protectedItem = Protect(metadata, draft.Title, draft.Body);
        try
        {
            await store.CreateAsync(protectedItem, cancellationToken).ConfigureAwait(false);
            return new MemoryItem(metadata, draft.Title, draft.Body);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            throw StorageFailure(exception);
        }
    }

    public async Task<MemoryItem> UpdateAsync(
        Guid id,
        int expectedVersion,
        MemoryDraft draft,
        DateTimeOffset? updatedAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        RequireIdentityAndVersion(id, expectedVersion);
        await ValidateProjectScopeAsync(draft.Scope, cancellationToken).ConfigureAwait(false);
        var current = await GetProtectedAsync(id, cancellationToken).ConfigureAwait(false);
        var metadata = MemoryMetadata.Create(
            id,
            draft.Category,
            draft.Scope,
            current.Metadata.Status,
            MemorySourceKind.UserExplicit,
            current.Metadata.CreatedAtUtc,
            updatedAtUtc ?? timeProvider.GetUtcNow(),
            draft.ExpiresAtUtc,
            1.0,
            expectedVersion + 1);
        var replacement = Protect(metadata, draft.Title, draft.Body);
        MemoryStoreMutationResult result;
        try
        {
            result = await store.UpdateAsync(replacement, expectedVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            throw StorageFailure(exception);
        }

        RequireApplied(result);
        return new MemoryItem(metadata, draft.Title, draft.Body);
    }

    public async Task<MemoryItem> SetEnabledAsync(
        Guid id,
        bool enabled,
        int expectedVersion,
        DateTimeOffset? updatedAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        RequireIdentityAndVersion(id, expectedVersion);
        var current = await GetProtectedAsync(id, cancellationToken).ConfigureAwait(false);
        if (enabled)
        {
            await ValidateProjectScopeAsync(current.Metadata.Scope, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            _ = Unprotect(current);
        }
        catch (Exception exception) when (IsProtectionFailure(exception))
        {
            throw ProtectionFailure(exception);
        }

        MemoryStoreMutationResult result;
        try
        {
            result = await store.SetEnabledAsync(
                id,
                enabled,
                expectedVersion,
                updatedAtUtc ?? timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            throw StorageFailure(exception);
        }

        var protectedItem = RequireApplied(result);
        try
        {
            return Unprotect(protectedItem);
        }
        catch (Exception exception) when (IsProtectionFailure(exception))
        {
            throw ProtectionFailure(exception);
        }
    }

    public async Task<MemoryItem> DeleteAsync(
        Guid id,
        int expectedVersion,
        bool confirmed,
        DateTimeOffset? updatedAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        RequireIdentityAndVersion(id, expectedVersion);
        if (!confirmed)
        {
            throw InvalidRequest("删除长期记忆前需要明确确认。");
        }

        MemoryStoreMutationResult result;
        try
        {
            result = await store.DeleteAsync(
                id,
                expectedVersion,
                updatedAtUtc ?? timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            throw StorageFailure(exception);
        }

        if (result.Disposition == MemoryStoreMutationDisposition.AlreadyDeleted
            && result.Current is { } tombstone)
        {
            return new MemoryItem(tombstone.Metadata, null, null);
        }

        var deleted = RequireApplied(result);
        return new MemoryItem(deleted.Metadata, null, null);
    }

    private async Task<ProtectedMemoryItem> GetProtectedAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var item = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
            return item is null || item.Metadata.Status == MemoryStatus.Deleted
                ? throw NotFound()
                : item;
        }
        catch (MemoryServiceException)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            throw StorageFailure(exception);
        }
    }

    private async Task ValidateProjectScopeAsync(
        MemoryScope scope,
        CancellationToken cancellationToken)
    {
        if (scope.Kind == MemoryScopeKind.Global)
        {
            return;
        }

        var projectId = scope.ProjectId ?? throw InvalidRequest();
        try
        {
            var project = await taskStore.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
            var authorization = await taskStore.GetProjectAuthorizationAsync(projectId, cancellationToken)
                .ConfigureAwait(false);
            if (project?.AuthorizationState != ProjectAuthorizationState.Authorized
                || authorization?.State != ProjectAuthorizationState.Authorized)
            {
                throw InvalidRequest("只能为当前已授权项目保存长期记忆。");
            }
        }
        catch (MemoryServiceException)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            throw StorageFailure(exception);
        }
    }

    private ProtectedMemoryItem Protect(MemoryMetadata metadata, string title, string body)
    {
        try
        {
            return new ProtectedMemoryItem(
                metadata,
                protector.Protect(title),
                protector.Protect(body),
                null);
        }
        catch (Exception exception) when (IsProtectionFailure(exception))
        {
            throw ProtectionFailure(exception);
        }
    }

    private MemoryItem Unprotect(ProtectedMemoryItem item)
    {
        if (item.Metadata.Status == MemoryStatus.Deleted)
        {
            return new MemoryItem(item.Metadata, null, null);
        }

        if (item.ProtectedTitle is not { Length: > 0 } title
            || item.ProtectedBody is not { Length: > 0 } body)
        {
            throw new InvalidDataException("受保护记忆内容缺失。");
        }

        return new MemoryItem(
            item.Metadata,
            protector.Unprotect(title),
            protector.Unprotect(body));
    }

    private static ProtectedMemoryItem RequireApplied(MemoryStoreMutationResult result) =>
        result.Disposition switch
        {
            MemoryStoreMutationDisposition.Applied when result.Current is { } current => current,
            MemoryStoreMutationDisposition.NotFound => throw NotFound(),
            MemoryStoreMutationDisposition.Conflict => throw Conflict(),
            MemoryStoreMutationDisposition.AlreadyDeleted => throw NotFound(),
            _ => throw StorageFailure(new InvalidOperationException("记忆存储返回了无效结果。"))
        };

    private static void RequireIdentityAndVersion(Guid id, int expectedVersion)
    {
        if (id == Guid.Empty || expectedVersion <= 0)
        {
            throw InvalidRequest();
        }
    }

    private static bool IsProtectionFailure(Exception exception) =>
        exception is CryptographicException
            or DecoderFallbackException
            or InvalidDataException
            or ArgumentException;

    private static bool IsStorageFailure(Exception exception) =>
        exception is not OperationCanceledException
        && exception is not MemoryServiceException;

    private static MemoryServiceException InvalidRequest(
        string message = "长期记忆请求不符合要求。") =>
        new(MemoryServiceErrorCodes.InvalidRequest, message);

    private static MemoryServiceException NotFound() =>
        new(MemoryServiceErrorCodes.NotFound, "没有找到这条长期记忆。");

    private static MemoryServiceException Conflict() =>
        new(MemoryServiceErrorCodes.Conflict, "这条长期记忆已被修改，请刷新后重试。");

    private static MemoryServiceException StorageFailure(Exception exception) =>
        new(MemoryServiceErrorCodes.StorageFailure, "长期记忆暂时无法保存或读取。", exception);

    private static MemoryServiceException ProtectionFailure(Exception exception) =>
        new(MemoryServiceErrorCodes.ProtectionFailure, "记忆内容无法安全读取。", exception);
}
