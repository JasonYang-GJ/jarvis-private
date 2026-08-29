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
    public async Task<MemoryOutboundPreparedConsent> PrepareOutboundAsync(
        MemoryOutboundPreparationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateOutboundRequest(request);
        var now = timeProvider.GetUtcNow();
        try
        {
            return await store.ExecuteOutboundSelectionAsync(
                request.Items,
                request.ProjectId,
                now,
                protectedItems => BuildPreparedOutbound(request, protectedItems, now),
                cancellationToken).ConfigureAwait(false);
        }
        catch (MemoryServiceException)
        {
            throw;
        }
        catch (MemoryValidationException exception)
        {
            throw new MemoryServiceException(
                exception.Message.Contains("授权", StringComparison.Ordinal)
                    ? MemoryOutboundErrorCodes.ProjectUnauthorized
                    : MemoryOutboundErrorCodes.ItemUnavailable,
                "所选记忆已变化或当前不可用于出站。",
                exception);
        }
        catch (Exception exception) when (IsProtectionFailure(exception))
        {
            throw ProtectionFailure(exception);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            throw new MemoryServiceException(
                MemoryOutboundErrorCodes.CommitFailed,
                "记忆出站准备未能安全完成。",
                exception);
        }
    }

    public async Task<MemoryOutboundEnvelope> CommitOutboundAsync(
        MemoryOutboundPreparedConsent prepared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var now = timeProvider.GetUtcNow();
        if (now > prepared.ExpiresAtUtc)
        {
            throw new MemoryServiceException(
                MemoryOutboundErrorCodes.ConsentStale,
                "记忆出站确认已失效，请重新检查后确认。");
        }

        try
        {
            return await store.ExecuteOutboundSelectionAsync(
                prepared.Items.Select(item => new MemoryOutboundItemReference(item.Id, item.Version)).ToArray(),
                prepared.ProjectId,
                now,
                protectedItems =>
                {
                    var current = protectedItems.Select(Unprotect).ToArray();
                    if (current.Length != prepared.Items.Count
                        || current.Where((item, index) => !Matches(prepared.Items[index], item)).Any())
                    {
                        throw new MemoryValidationException("记忆出站确认快照已变化。");
                    }

                    return new MemoryOutboundEnvelope(
                        prepared.SerializedContext,
                        prepared.ToAuditMetadata(now));
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (MemoryServiceException)
        {
            throw;
        }
        catch (MemoryValidationException exception)
        {
            throw new MemoryServiceException(
                exception.Message.Contains("授权", StringComparison.Ordinal)
                    ? MemoryOutboundErrorCodes.ProjectUnauthorized
                    : MemoryOutboundErrorCodes.ConsentStale,
                exception.Message.Contains("授权", StringComparison.Ordinal)
                    ? "所选项目已不再授权，记忆没有发送。"
                    : "记忆出站确认已失效，请重新检查后确认。",
                exception);
        }
        catch (Exception exception) when (IsProtectionFailure(exception))
        {
            throw ProtectionFailure(exception);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            throw new MemoryServiceException(
                MemoryOutboundErrorCodes.CommitFailed,
                "记忆出站提交未能安全完成。",
                exception);
        }
    }

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

    public async Task<MemoryPreviewResult> PreviewAsync(
        string query,
        Guid? projectId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = MemoryPreviewRanker.CanonicalizeQuery(query);
            if (projectId == Guid.Empty)
            {
                throw new MemoryValidationException("项目 ID 无效。");
            }
        }
        catch (MemoryValidationException exception)
        {
            throw InvalidRequest("本地记忆预览请求不符合要求。", exception);
        }

        if (projectId is { } exactProjectId)
        {
            await ValidateProjectScopeAsync(
                MemoryScope.ForProject(exactProjectId),
                cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<ProtectedMemoryItem> protectedItems;
        try
        {
            protectedItems = await store.ListRetrievalCandidatesAsync(
                timeProvider.GetUtcNow(),
                projectId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            throw StorageFailure(exception);
        }

        MemoryItem[] items;
        try
        {
            items = protectedItems.Select(Unprotect).ToArray();
        }
        catch (Exception exception) when (IsProtectionFailure(exception))
        {
            throw ProtectionFailure(exception);
        }

        return MemoryPreviewRanker.Preview(query, items, projectId);
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
        string message = "长期记忆请求不符合要求。",
        Exception? innerException = null) =>
        new(MemoryServiceErrorCodes.InvalidRequest, message, innerException);

    private static MemoryServiceException NotFound() =>
        new(MemoryServiceErrorCodes.NotFound, "没有找到这条长期记忆。");

    private static MemoryServiceException Conflict() =>
        new(MemoryServiceErrorCodes.Conflict, "这条长期记忆已被修改，请刷新后重试。");

    private static MemoryServiceException StorageFailure(Exception exception) =>
        new(MemoryServiceErrorCodes.StorageFailure, "长期记忆暂时无法保存或读取。", exception);

    private static MemoryServiceException ProtectionFailure(Exception exception) =>
        new(MemoryServiceErrorCodes.ProtectionFailure, "记忆内容无法安全读取。", exception);

    private static void ValidateOutboundRequest(MemoryOutboundPreparationRequest request)
    {
        if (request.CoordinatorInstanceId == Guid.Empty
            || request.SessionId == Guid.Empty
            || request.TurnId == Guid.Empty
            || request.TurnVersion < 0
            || string.IsNullOrWhiteSpace(request.ProviderId)
            || string.IsNullOrWhiteSpace(request.ModelId)
            || string.IsNullOrWhiteSpace(request.PromptId)
            || string.IsNullOrWhiteSpace(request.PromptVersion)
            || string.IsNullOrWhiteSpace(request.PromptContentHash)
            || request.ProjectId == Guid.Empty
            || request.Items is null
            || request.Items.Count is < MemoryOutboundLimits.MinimumItems or > MemoryOutboundLimits.MaximumItems
            || request.Items.Select(item => item.MemoryId).Distinct().Count() != request.Items.Count)
        {
            throw InvalidRequest("记忆出站请求不符合要求。");
        }

        try
        {
            _ = MemoryOutboundContract.NormalizeHttpsOrigin(request.DataDestination);
        }
        catch (MemoryValidationException exception)
        {
            throw new MemoryServiceException(
                MemoryOutboundErrorCodes.DestinationUnverifiable,
                "当前普通聊天目的地无法安全验证，未发送记忆。",
                exception);
        }
    }

    private MemoryOutboundPreparedConsent BuildPreparedOutbound(
        MemoryOutboundPreparationRequest request,
        IReadOnlyList<ProtectedMemoryItem> protectedItems,
        DateTimeOffset now)
    {
        var items = protectedItems.Select(Unprotect).Select(item =>
        {
            var title = item.Title ?? throw new InvalidDataException("记忆标题缺失。");
            var body = item.Body ?? throw new InvalidDataException("记忆正文缺失。");
            return new MemoryOutboundPreparedItem(
                item.Metadata.Id,
                item.Metadata.Version,
                item.Metadata.Category,
                item.Metadata.Scope.Kind,
                title,
                body,
                title.Length + body.Length);
        }).ToArray();
        var totalCharacters = items.Sum(item => item.CharacterCount);
        if (totalCharacters > MemoryOutboundLimits.MaximumContentCharacters)
        {
            throw new MemoryServiceException(
                MemoryOutboundErrorCodes.BudgetExceeded,
                "所选记忆内容超过单次出站限制，请减少选择。");
        }

        var serialized = MemoryOutboundContract.SerializeContext(items);
        if (serialized.Length > MemoryOutboundLimits.MaximumSerializedContextCharacters)
        {
            throw new MemoryServiceException(
                MemoryOutboundErrorCodes.BudgetExceeded,
                "所选记忆序列化后超过单次出站限制，请减少选择。");
        }

        var destinationOrigin = MemoryOutboundContract.NormalizeHttpsOrigin(request.DataDestination);
        return new MemoryOutboundPreparedConsent
        {
            ConsentId = Guid.NewGuid(),
            CoordinatorInstanceId = request.CoordinatorInstanceId,
            SessionId = request.SessionId,
            TurnId = request.TurnId,
            TurnVersion = request.TurnVersion,
            PreparedAtUtc = now,
            ExpiresAtUtc = now.Add(MemoryOutboundLimits.MaximumPreparedLifetime),
            ProviderId = request.ProviderId.Trim(),
            ModelId = request.ModelId.Trim(),
            DestinationOrigin = destinationOrigin,
            PromptId = request.PromptId.Trim(),
            PromptVersion = request.PromptVersion.Trim(),
            PromptContentHash = request.PromptContentHash.Trim(),
            ProjectId = request.ProjectId,
            Items = items,
            TotalCharacters = totalCharacters,
            SerializedContext = serialized,
            ManifestHash = MemoryOutboundContract.CreateManifestHash(
                request,
                destinationOrigin,
                items,
                totalCharacters)
        };
    }

    private static bool Matches(MemoryOutboundPreparedItem prepared, MemoryItem current) =>
        prepared.Id == current.Metadata.Id
        && prepared.Version == current.Metadata.Version
        && prepared.Category == current.Metadata.Category
        && prepared.Scope == current.Metadata.Scope.Kind
        && string.Equals(prepared.Title, current.Title, StringComparison.Ordinal)
        && string.Equals(prepared.Body, current.Body, StringComparison.Ordinal);
}
