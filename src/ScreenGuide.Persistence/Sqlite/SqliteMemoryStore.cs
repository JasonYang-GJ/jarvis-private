using System.Globalization;
using Microsoft.Data.Sqlite;
using ScreenGuide.Core.Memories;
using ScreenGuide.Core.Tasking;

namespace ScreenGuide.Persistence.Sqlite;

public sealed class SqliteMemoryStore : IMemoryStore
{
    public const int MaximumRetrievalCandidates = 200;

    private readonly string _connectionString;

    public SqliteMemoryStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("数据库路径不能为空。", nameof(databasePath));
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath.Trim()),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_info;";
        var version = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (version != V02Contract.SchemaVersion)
        {
            throw new NotSupportedException(
                $"记忆存储要求数据库版本 {V02Contract.SchemaVersion}，当前为 {version}。");
        }

        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'memory_items';";
        if (Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidOperationException("记忆存储结构不存在。");
        }
    }

    public async Task<IReadOnlyList<ProtectedMemoryItem>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM memory_items
            WHERE status != 'Deleted'
            ORDER BY updated_at_utc DESC, id;
            """;
        var items = new List<ProtectedMemoryItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(Read(reader));
        }

        return items;
    }

    public async Task<IReadOnlyList<ProtectedMemoryItem>> ListRetrievalCandidatesAsync(
        DateTimeOffset nowUtc,
        Guid? projectId,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
        {
            throw new MemoryValidationException("项目 ID 无效。");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = projectId is null
            ? """
                SELECT * FROM memory_items
                WHERE status = 'Active'
                  AND (expires_at_utc IS NULL OR expires_at_utc > $nowUtc)
                  AND scope_kind = 'Global'
                ORDER BY updated_at_utc DESC, id
                LIMIT 200;
                """
            : """
                SELECT * FROM memory_items
                WHERE status = 'Active'
                  AND (expires_at_utc IS NULL OR expires_at_utc > $nowUtc)
                  AND (
                       scope_kind = 'Global'
                       OR (scope_kind = 'Project' AND project_id = $projectId)
                  )
                ORDER BY updated_at_utc DESC, id
                LIMIT 200;
                """;
        command.Parameters.AddWithValue("$nowUtc", ToDb(nowUtc));
        if (projectId is { } exactProjectId)
        {
            command.Parameters.AddWithValue("$projectId", exactProjectId.ToString("D"));
        }

        var items = new List<ProtectedMemoryItem>(MaximumRetrievalCandidates);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(Read(reader));
        }

        return items;
    }

    public async Task<ProtectedMemoryItem?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await GetAsync(connection, null, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TResult> ExecuteOutboundSelectionAsync<TResult>(
        IReadOnlyList<MemoryOutboundItemReference> references,
        Guid? projectId,
        DateTimeOffset nowUtc,
        Func<IReadOnlyList<ProtectedMemoryItem>, TResult> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(action);
        if (projectId == Guid.Empty
            || references.Count is < MemoryOutboundLimits.MinimumItems or > MemoryOutboundLimits.MaximumItems
            || references.Select(item => item.MemoryId).Distinct().Count() != references.Count)
        {
            throw new MemoryValidationException("记忆出站选择无效。");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (projectId is { } exactProjectId)
        {
            await using var projectCommand = connection.CreateCommand();
            projectCommand.Transaction = transaction;
            projectCommand.CommandText = """
                SELECT COUNT(*)
                FROM projects
                INNER JOIN project_authorizations
                    ON project_authorizations.project_id = projects.id
                WHERE projects.id = $projectId
                  AND projects.authorization_state = 'Authorized'
                  AND project_authorizations.state = 'Authorized';
                """;
            projectCommand.Parameters.AddWithValue("$projectId", exactProjectId.ToString("D"));
            if (Convert.ToInt32(
                    await projectCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture) != 1)
            {
                throw new MemoryValidationException("记忆出站项目未获授权。");
            }
        }

        var selected = new List<ProtectedMemoryItem>(references.Count);
        foreach (var reference in references)
        {
            var item = await GetAsync(connection, transaction, reference.MemoryId, cancellationToken)
                .ConfigureAwait(false);
            if (item is null
                || item.Metadata.Version != reference.ExpectedVersion
                || item.Metadata.Status != MemoryStatus.Active
                || item.Metadata.ExpiresAtUtc is { } expires && expires <= nowUtc
                || item.Metadata.Scope.Kind == MemoryScopeKind.Project
                    && item.Metadata.Scope.ProjectId != projectId)
            {
                throw new MemoryValidationException("记忆出站项目已变化或不可用。");
            }

            selected.Add(item);
        }

        var result = action(selected);
        transaction.Commit();
        return result;
    }

    public async Task CreateAsync(
        ProtectedMemoryItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateWritable(item, expectedVersion: 1);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_items(
                id, category, scope_kind, project_id, protected_title, protected_body,
                source_kind, source_reference, created_at_utc, updated_at_utc,
                expires_at_utc, status, confidence, version)
            VALUES(
                $id, $category, $scopeKind, $projectId, $protectedTitle, $protectedBody,
                $sourceKind, $sourceReference, $createdAtUtc, $updatedAtUtc,
                $expiresAtUtc, $status, $confidence, $version);
            """;
        AddItem(command, item);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MemoryStoreMutationResult> UpdateAsync(
        ProtectedMemoryItem item,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateWritableContent(item);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var current = await GetAsync(connection, transaction, item.Metadata.Id, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return new MemoryStoreMutationResult(MemoryStoreMutationDisposition.NotFound, null);
        }

        if (current.Metadata.Version != expectedVersion || current.Metadata.Status == MemoryStatus.Deleted)
        {
            return new MemoryStoreMutationResult(MemoryStoreMutationDisposition.Conflict, current);
        }

        if (item.Metadata.Version != expectedVersion + 1)
        {
            throw new MemoryValidationException("更新后的记忆版本必须连续递增。");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE memory_items SET
                category = $category,
                scope_kind = $scopeKind,
                project_id = $projectId,
                protected_title = $protectedTitle,
                protected_body = $protectedBody,
                source_kind = $sourceKind,
                source_reference = $sourceReference,
                updated_at_utc = $updatedAtUtc,
                expires_at_utc = $expiresAtUtc,
                status = $status,
                confidence = $confidence,
                version = $version
            WHERE id = $id AND version = $expectedVersion AND status != 'Deleted';
            """;
        AddItem(command, item);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("记忆更新未能保持原子版本合同。");
        }

        transaction.Commit();
        return new MemoryStoreMutationResult(MemoryStoreMutationDisposition.Applied, item);
    }

    public Task<MemoryStoreMutationResult> SetEnabledAsync(
        Guid id,
        bool enabled,
        int expectedVersion,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default) => MutateStatusAsync(
        id,
        enabled ? MemoryStatus.Active : MemoryStatus.Disabled,
        expectedVersion,
        updatedAtUtc,
        deleteContent: false,
        cancellationToken);

    public Task<MemoryStoreMutationResult> DeleteAsync(
        Guid id,
        int expectedVersion,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default) => MutateStatusAsync(
        id,
        MemoryStatus.Deleted,
        expectedVersion,
        updatedAtUtc,
        deleteContent: true,
        cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<MemoryStoreMutationResult> MutateStatusAsync(
        Guid id,
        MemoryStatus status,
        int expectedVersion,
        DateTimeOffset updatedAtUtc,
        bool deleteContent,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var current = await GetAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return new MemoryStoreMutationResult(MemoryStoreMutationDisposition.NotFound, null);
        }

        if (current.Metadata.Status == MemoryStatus.Deleted)
        {
            return new MemoryStoreMutationResult(MemoryStoreMutationDisposition.AlreadyDeleted, current);
        }

        if (current.Metadata.Version != expectedVersion)
        {
            return new MemoryStoreMutationResult(MemoryStoreMutationDisposition.Conflict, current);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = deleteContent
            ? """
                UPDATE memory_items SET
                    protected_title = NULL,
                    protected_body = NULL,
                    source_reference = NULL,
                    expires_at_utc = NULL,
                    status = $status,
                    updated_at_utc = $updatedAtUtc,
                    version = version + 1
                WHERE id = $id AND version = $expectedVersion AND status != 'Deleted';
                """
            : """
                UPDATE memory_items SET
                    status = $status,
                    updated_at_utc = $updatedAtUtc,
                    version = version + 1
                WHERE id = $id AND version = $expectedVersion AND status != 'Deleted';
                """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$updatedAtUtc", ToDb(updatedAtUtc));
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("记忆状态更新未能保持原子版本合同。");
        }

        var updated = await GetAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("记忆状态更新后无法读取记录。");
        transaction.Commit();
        return new MemoryStoreMutationResult(MemoryStoreMutationDisposition.Applied, updated);
    }

    private Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        SqliteConnectionOpener.OpenAsync(_connectionString, cancellationToken);

    private static async Task<ProtectedMemoryItem?> GetAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM memory_items WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static ProtectedMemoryItem Read(SqliteDataReader reader)
    {
        var kind = Enum.Parse<MemoryScopeKind>(reader.GetString(reader.GetOrdinal("scope_kind")));
        var projectOrdinal = reader.GetOrdinal("project_id");
        var scope = kind == MemoryScopeKind.Global
            ? MemoryScope.Global
            : MemoryScope.ForProject(Guid.Parse(reader.GetString(projectOrdinal)));
        var metadata = MemoryMetadata.Create(
            Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
            Enum.Parse<MemoryCategory>(reader.GetString(reader.GetOrdinal("category"))),
            scope,
            Enum.Parse<MemoryStatus>(reader.GetString(reader.GetOrdinal("status"))),
            Enum.Parse<MemorySourceKind>(reader.GetString(reader.GetOrdinal("source_kind"))),
            FromDb(reader.GetString(reader.GetOrdinal("created_at_utc"))),
            FromDb(reader.GetString(reader.GetOrdinal("updated_at_utc"))),
            ReadNullableDate(reader, "expires_at_utc"),
            reader.GetDouble(reader.GetOrdinal("confidence")),
            reader.GetInt32(reader.GetOrdinal("version")));
        return new ProtectedMemoryItem(
            metadata,
            ReadNullableBytes(reader, "protected_title"),
            ReadNullableBytes(reader, "protected_body"),
            ReadNullableString(reader, "source_reference"));
    }

    private static void ValidateWritable(ProtectedMemoryItem item, int expectedVersion)
    {
        if (item.Metadata.Version != expectedVersion
            || item.Metadata.Status == MemoryStatus.Deleted)
        {
            throw new MemoryValidationException("待写入的受保护记忆无效。");
        }

        ValidateWritableContent(item);
    }

    private static void ValidateWritableContent(ProtectedMemoryItem item)
    {
        if (item.Metadata.Status == MemoryStatus.Deleted
            || item.ProtectedTitle is not { Length: > 0 }
            || item.ProtectedBody is not { Length: > 0 })
        {
            throw new MemoryValidationException("待写入的受保护记忆无效。");
        }
    }

    private static void AddItem(SqliteCommand command, ProtectedMemoryItem item)
    {
        command.Parameters.AddWithValue("$id", item.Metadata.Id.ToString("D"));
        command.Parameters.AddWithValue("$category", item.Metadata.Category.ToString());
        command.Parameters.AddWithValue("$scopeKind", item.Metadata.Scope.Kind.ToString());
        command.Parameters.AddWithValue(
            "$projectId",
            item.Metadata.Scope.ProjectId?.ToString("D") as object ?? DBNull.Value);
        command.Parameters.Add("$protectedTitle", SqliteType.Blob).Value = item.ProtectedTitle!;
        command.Parameters.Add("$protectedBody", SqliteType.Blob).Value = item.ProtectedBody!;
        command.Parameters.AddWithValue("$sourceKind", item.Metadata.SourceKind.ToString());
        command.Parameters.AddWithValue("$sourceReference", item.SourceReference as object ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", ToDb(item.Metadata.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedAtUtc", ToDb(item.Metadata.UpdatedAtUtc));
        command.Parameters.AddWithValue(
            "$expiresAtUtc",
            item.Metadata.ExpiresAtUtc is { } expires ? ToDb(expires) : DBNull.Value);
        command.Parameters.AddWithValue("$status", item.Metadata.Status.ToString());
        command.Parameters.AddWithValue("$confidence", item.Metadata.Confidence);
        command.Parameters.AddWithValue("$version", item.Metadata.Version);
    }

    private static byte[]? ReadNullableBytes(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : (byte[])reader.GetValue(ordinal);
    }

    private static string? ReadNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, string name)
    {
        var value = ReadNullableString(reader, name);
        return value is null ? null : FromDb(value);
    }

    private static string ToDb(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset FromDb(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
