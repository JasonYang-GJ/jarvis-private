using Microsoft.Data.Sqlite;
using ScreenGuide.Core.Memories;
using ScreenGuide.Core.Tasking;
using ScreenGuide.Persistence.Sqlite;

namespace ScreenGuide.Tasking.Tests;

public sealed class SqliteMemoryStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Version8DatabaseMigratesAtomicallyToVersion9AndKeepsAPreVersion9Backup()
    {
        var root = NewRoot();
        var databasePath = Path.Combine(root, "state", "tasking.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        try
        {
            await CreateVersion8DatabaseAsync(databasePath);

            await using var store = new SqliteTaskStore(databasePath);
            await store.InitializeAsync();

            Assert.Equal(9, await store.GetSchemaVersionAsync());
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            Assert.True(await ObjectExistsAsync(connection, "table", "memory_items"));
            Assert.True(await ObjectExistsAsync(connection, "index", "ix_memory_items_status_expiry"));
            Assert.True(await ObjectExistsAsync(connection, "index", "ix_memory_items_project"));
            Assert.Equal("v8-device-canary", await ReadDeviceCanaryAsync(connection));

            var backup = Assert.Single(Directory.GetFiles(
                Path.GetDirectoryName(databasePath)!,
                "tasking.pre-v9-from-v8-*.backup.db"));
            await using var backupConnection = new SqliteConnection($"Data Source={backup};Mode=ReadOnly");
            await backupConnection.OpenAsync();
            Assert.Equal(8, await ReadSchemaVersionAsync(backupConnection));
            Assert.False(await ObjectExistsAsync(backupConnection, "table", "memory_items"));
            Assert.Equal("v8-device-canary", await ReadDeviceCanaryAsync(backupConnection));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedVersion9MigrationRollsBackAndLeavesVersion8AndBackupIntact()
    {
        var root = NewRoot();
        var databasePath = Path.Combine(root, "state", "tasking.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        try
        {
            await CreateVersion8DatabaseAsync(databasePath, addConflictingMemoryTable: true);

            await using var store = new SqliteTaskStore(databasePath);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.InitializeAsync());

            Assert.Contains("备份", failure.Message, StringComparison.Ordinal);
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            Assert.Equal(8, await ReadSchemaVersionAsync(connection));
            Assert.False(await ObjectExistsAsync(connection, "index", "ix_memory_items_status_expiry"));
            Assert.Equal("v8-device-canary", await ReadDeviceCanaryAsync(connection));
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT marker FROM memory_items;";
            Assert.Equal("v8-canary", await command.ExecuteScalarAsync());
            Assert.Single(Directory.GetFiles(
                Path.GetDirectoryName(databasePath)!,
                "tasking.pre-v9-from-v8-*.backup.db"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProtectedCrudUsesOptimisticConcurrencyDisableExpiryAndContentFreeTombstone()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        await using var store = new SqliteMemoryStore(environment.DatabasePath);
        await store.InitializeAsync();
        var id = Guid.NewGuid();
        var original = NewProtectedItem(id, version: 1);

        await store.CreateAsync(original);
        var created = await store.GetAsync(id);

        Assert.Equal(original.ProtectedTitle, created?.ProtectedTitle);
        Assert.Equal(original.ProtectedBody, created?.ProtectedBody);
        Assert.Single(await store.ListAsync());

        var conflict = await store.UpdateAsync(
            NewProtectedItem(id, version: 2, title: [9], body: [8]),
            expectedVersion: 7);
        Assert.Equal(MemoryStoreMutationDisposition.Conflict, conflict.Disposition);
        Assert.Equal(1, conflict.Current?.Metadata.Version);

        var update = await store.UpdateAsync(
            NewProtectedItem(id, version: 2, title: [9], body: [8]),
            expectedVersion: 1);
        Assert.Equal(MemoryStoreMutationDisposition.Applied, update.Disposition);
        Assert.Equal(2, update.Current?.Metadata.Version);

        var disabled = await store.SetEnabledAsync(id, false, 2, Now.AddMinutes(2));
        Assert.Equal(MemoryStatus.Disabled, disabled.Current?.Metadata.Status);
        Assert.False(disabled.Current?.Metadata is { } disabledMetadata
            && new MemoryItem(disabledMetadata, "title", "body").IsRetrievalCandidate(Now));

        var deleted = await store.DeleteAsync(id, 3, Now.AddMinutes(3));
        Assert.Equal(MemoryStoreMutationDisposition.Applied, deleted.Disposition);
        Assert.Equal(MemoryStatus.Deleted, deleted.Current?.Metadata.Status);
        Assert.Null(deleted.Current?.ProtectedTitle);
        Assert.Null(deleted.Current?.ProtectedBody);
        Assert.Null(deleted.Current?.SourceReference);
        Assert.Equal(4, deleted.Current?.Metadata.Version);

        var idempotentDelete = await store.DeleteAsync(id, 4, Now.AddMinutes(4));
        Assert.Equal(MemoryStoreMutationDisposition.AlreadyDeleted, idempotentDelete.Disposition);
        Assert.Equal(4, idempotentDelete.Current?.Metadata.Version);
    }

    [Fact]
    public async Task RetrievalCandidateReadReturnsOnlyActiveUnexpiredGlobalAndExactProjectWithoutMutation()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        await using var store = new SqliteMemoryStore(environment.DatabasePath);
        await store.InitializeAsync();
        var global = NewProtectedItem(
            Guid.Parse("00000000-0000-0000-0000-000000000010"),
            version: 1,
            updatedAtUtc: Now.AddMinutes(1));
        var project = NewProtectedItem(
            Guid.Parse("00000000-0000-0000-0000-000000000011"),
            version: 1,
            scope: MemoryScope.ForProject(environment.Project.Id),
            updatedAtUtc: Now.AddMinutes(2));
        var expired = NewProtectedItem(
            Guid.NewGuid(),
            version: 1,
            expiresAtUtc: Now);
        var disabled = NewProtectedItem(Guid.NewGuid(), version: 1);
        var deleted = NewProtectedItem(Guid.NewGuid(), version: 1);
        await store.CreateAsync(global);
        await store.CreateAsync(project);
        await store.CreateAsync(expired);
        await store.CreateAsync(disabled);
        await store.CreateAsync(deleted);
        _ = await store.SetEnabledAsync(disabled.Metadata.Id, false, 1, Now.AddMinutes(3));
        _ = await store.DeleteAsync(deleted.Metadata.Id, 1, Now.AddMinutes(3));

        var globalOnly = await store.ListRetrievalCandidatesAsync(Now, projectId: null);
        var withProject = await store.ListRetrievalCandidatesAsync(Now, environment.Project.Id);
        var persistedGlobal = await store.GetAsync(global.Metadata.Id);
        var persistedProject = await store.GetAsync(project.Metadata.Id);

        Assert.Equal([global.Metadata.Id], globalOnly.Select(item => item.Metadata.Id));
        Assert.Equal(
            [project.Metadata.Id, global.Metadata.Id],
            withProject.Select(item => item.Metadata.Id));
        Assert.Equal(1, persistedGlobal?.Metadata.Version);
        Assert.Equal(global.Metadata.UpdatedAtUtc, persistedGlobal?.Metadata.UpdatedAtUtc);
        Assert.Equal(1, persistedProject?.Metadata.Version);
        Assert.Equal(project.Metadata.UpdatedAtUtc, persistedProject?.Metadata.UpdatedAtUtc);
    }

    [Fact]
    public async Task RetrievalCandidateReadCapsAtTwoHundredAndUsesStableStoreOrder()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        await using var store = new SqliteMemoryStore(environment.DatabasePath);
        await store.InitializeAsync();
        var items = Enumerable.Range(1, 205)
            .Select(index => NewProtectedItem(
                Guid.Parse($"00000000-0000-0000-0000-{index:D12}"),
                version: 1,
                updatedAtUtc: Now.AddMinutes(index)))
            .ToArray();
        foreach (var item in items)
        {
            await store.CreateAsync(item);
        }

        var candidates = await store.ListRetrievalCandidatesAsync(Now, projectId: null);

        Assert.Equal(SqliteMemoryStore.MaximumRetrievalCandidates, candidates.Count);
        Assert.Equal(
            items.OrderByDescending(item => item.Metadata.UpdatedAtUtc)
                .Take(SqliteMemoryStore.MaximumRetrievalCandidates)
                .Select(item => item.Metadata.Id),
            candidates.Select(item => item.Metadata.Id));
    }

    private static ProtectedMemoryItem NewProtectedItem(
        Guid id,
        int version,
        byte[]? title = null,
        byte[]? body = null,
        MemoryScope? scope = null,
        DateTimeOffset? updatedAtUtc = null,
        DateTimeOffset? expiresAtUtc = null) => new(
        MemoryMetadata.Create(
            id,
            MemoryCategory.ProjectNote,
            scope ?? MemoryScope.Global,
            MemoryStatus.Active,
            MemorySourceKind.UserExplicit,
            Now,
            updatedAtUtc ?? Now.AddMinutes(version - 1),
            expiresAtUtc ?? Now.AddDays(1),
            1.0,
            version),
        title ?? [1, 2, 3],
        body ?? [4, 5, 6],
        null);

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"screen-guide-memory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task CreateVersion8DatabaseAsync(
        string databasePath,
        bool addConflictingMemoryTable = false)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SqliteSchema.CreateVersion1
            + SqliteSchema.CreateVersion2
            + SqliteSchema.CreateVersion3
            + SqliteSchema.CreateVersion4
            + SqliteSchema.CreateVersion5
            + SqliteSchema.CreateVersion6
            + SqliteSchema.CreateVersion7
            + SqliteSchema.CreateVersion8
            + "INSERT INTO schema_info(version, applied_at_utc) VALUES "
            + string.Join(",", Enumerable.Range(1, 8).Select(version => $"({version}, '2026-08-29T00:00:00Z')"))
            + ";"
            + "INSERT INTO devices(id, display_name, device_type, trust_state, created_at_utc) "
            + "VALUES('11111111-1111-1111-1111-111111111111', 'v8-device-canary', "
            + "'WindowsHost', 'Local', '2026-08-29T00:00:00Z');";
        if (addConflictingMemoryTable)
        {
            command.CommandText += "CREATE TABLE memory_items(marker TEXT NOT NULL);"
                + "INSERT INTO memory_items(marker) VALUES('v8-canary');";
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ReadSchemaVersionAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM schema_info;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ReadDeviceCanaryAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT display_name FROM devices WHERE id = '11111111-1111-1111-1111-111111111111';";
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<bool> ObjectExistsAsync(
        SqliteConnection connection,
        string type,
        string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = $type AND name = $name;";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }
}
