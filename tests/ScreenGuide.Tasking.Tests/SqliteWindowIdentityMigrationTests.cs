using Microsoft.Data.Sqlite;
using ScreenGuide.Core.Sessions;
using ScreenGuide.Core.Tasking;
using ScreenGuide.Persistence.Sqlite;

namespace ScreenGuide.Tasking.Tests;

public sealed class SqliteWindowIdentityMigrationTests
{
    [Fact]
    public async Task Version10MigratesAtomicallyToVersion11AndKeepsPreVersion11Backup()
    {
        var root = NewRoot();
        var databasePath = Path.Combine(root, "state", "tasking.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        try
        {
            await CreateVersion10DatabaseAsync(databasePath);

            await using var store = new SqliteTaskStore(databasePath);
            await store.InitializeAsync();

            Assert.Equal(11, await store.GetSchemaVersionAsync());
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            Assert.True(await ColumnExistsAsync(connection, "session_turns", "window_process_id"));
            Assert.True(await ColumnExistsAsync(connection, "session_turns", "window_process_started_at_utc"));

            var backup = Assert.Single(Directory.GetFiles(
                Path.GetDirectoryName(databasePath)!,
                "tasking.pre-v11-from-v10-*.backup.db"));
            await using var backupConnection = new SqliteConnection($"Data Source={backup};Mode=ReadOnly");
            await backupConnection.OpenAsync();
            Assert.Equal(10, await ReadSchemaVersionAsync(backupConnection));
            Assert.False(await ColumnExistsAsync(
                backupConnection,
                "session_turns",
                "window_process_id"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedVersion11MigrationLeavesVersion10AndBackupIntact()
    {
        var root = NewRoot();
        var databasePath = Path.Combine(root, "state", "tasking.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        try
        {
            await CreateVersion10DatabaseAsync(databasePath, addConflictingColumn: true);

            await using var store = new SqliteTaskStore(databasePath);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.InitializeAsync());

            Assert.Contains("备份", failure.Message, StringComparison.Ordinal);
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            Assert.Equal(10, await ReadSchemaVersionAsync(connection));
            Assert.True(await ColumnExistsAsync(connection, "session_turns", "window_process_id"));
            Assert.False(await ColumnExistsAsync(
                connection,
                "session_turns",
                "window_process_started_at_utc"));
            Assert.Single(Directory.GetFiles(
                Path.GetDirectoryName(databasePath)!,
                "tasking.pre-v11-from-v10-*.backup.db"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SessionStoreRoundTripsWindowIdentityAndKeepsHistoricalIdentityMissing()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        await using var sessions = new SqliteSessionStore(environment.DatabasePath);
        await sessions.InitializeAsync();
        var now = new DateTimeOffset(2026, 8, 30, 3, 0, 0, TimeSpan.Zero);
        var conversation = new ScreenGuide.Core.Conversations.ConversationRecord
        {
            Id = Guid.NewGuid(),
            CreatedByDeviceId = environment.Device.Id,
            Title = "window identity",
            ProviderId = "local",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var conversations = new SqliteConversationStore(environment.DatabasePath);
        await conversations.InitializeAsync();
        await conversations.CreateConversationAsync(conversation);
        var session = new SessionRecord
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            CreatedByDeviceId = environment.Device.Id,
            Title = "window identity",
            IsCurrent = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastActiveAtUtc = now
        };
        await sessions.CreateSessionAsync(session);
        var registration = await sessions.StartTurnAsync(
            session.Id,
            "看看窗口",
            "Text",
            "window-identity-v2",
            new SessionTurnFrozenRoute
            {
                Status = SessionTurnRouteStatus.Ready,
                ProviderId = "local",
                ModelId = "local",
                DataDestination = "local",
                SendsDataOffDevice = false,
                FrozenAtUtc = now
            },
            now);
        var started = new DateTimeOffset(2026, 8, 30, 2, 59, 0, TimeSpan.Zero);
        var updated = await sessions.UpdateTurnAsync(
            registration.Turn with
            {
                WindowHandle = 9001,
                WindowTitle = "目标窗口",
                WindowProcessName = "target",
                WindowProcessId = 3210,
                WindowProcessStartTimeUtc = started
            },
            registration.Turn.Version,
            now.AddSeconds(1));

        await using var reopened = new SqliteSessionStore(environment.DatabasePath);
        await reopened.InitializeAsync();
        var stored = await reopened.GetTurnAsync(updated.Id);

        Assert.Equal(3210, stored?.WindowProcessId);
        Assert.Equal(started, stored?.WindowProcessStartTimeUtc);

        await using var connection = new SqliteConnection($"Data Source={environment.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE session_turns
            SET window_process_id = NULL, window_process_started_at_utc = NULL
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", updated.Id.ToString("D"));
        await command.ExecuteNonQueryAsync();
        var historical = await reopened.GetTurnAsync(updated.Id);
        Assert.Null(historical?.WindowProcessId);
        Assert.Null(historical?.WindowProcessStartTimeUtc);
        await connection.CloseAsync();
        SqliteConnection.ClearAllPools();
    }

    private static async Task CreateVersion10DatabaseAsync(
        string path,
        bool addConflictingColumn = false)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE schema_info (
                version INTEGER NOT NULL PRIMARY KEY,
                applied_at_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync();
        var migrations = new[]
        {
            SqliteSchema.CreateVersion1,
            SqliteSchema.CreateVersion2,
            SqliteSchema.CreateVersion3,
            SqliteSchema.CreateVersion4,
            SqliteSchema.CreateVersion5,
            SqliteSchema.CreateVersion6,
            SqliteSchema.CreateVersion7,
            SqliteSchema.CreateVersion8,
            SqliteSchema.CreateVersion9,
            SqliteSchema.CreateVersion10
        };
        for (var index = 0; index < migrations.Length; index++)
        {
            command.CommandText = migrations[index];
            await command.ExecuteNonQueryAsync();
            command.CommandText =
                "INSERT INTO schema_info(version, applied_at_utc) VALUES($version, '2026-08-30T00:00:00Z');";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$version", index + 1);
            await command.ExecuteNonQueryAsync();
        }

        if (addConflictingColumn)
        {
            command.Parameters.Clear();
            command.CommandText = "ALTER TABLE session_turns ADD COLUMN window_process_id INTEGER NULL;";
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string table,
        string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}') WHERE name = $column;";
        command.Parameters.AddWithValue("$column", column);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<int> ReadSchemaVersionAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_info;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"screen-guide-v11-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
