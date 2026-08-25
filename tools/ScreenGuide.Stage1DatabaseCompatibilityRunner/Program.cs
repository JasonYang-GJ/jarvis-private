using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Sessions;
using ScreenGuide.Core.Tasking;
using ScreenGuide.Persistence.Sqlite;

namespace ScreenGuide.Stage1DatabaseCompatibilityRunner;

internal static class Program
{
    private const string ResultPrefix = "STAGE1_DB_COMPAT_RESULT ";
    private const string OwnerMarkerName = ".screen-guide-p0-05-owner";
    private const string OwnedDirectoryPrefix = "screen-guide-p0-05-";
    private const string DeviceDisplayName = "Stage 1 compatibility device";
    private const string ConversationTitle = "Stage 1 compatibility conversation";
    private const string UserMessage = "Stage 1 compatibility user canary";
    private const string AssistantMessage = "Stage 1 compatibility assistant canary";
    private const string SessionTitle = "Stage 1 compatibility session";
    private const string SessionInput = "Stage 1 compatibility session turn canary";
    private static readonly Guid DeviceId = Guid.Parse("10000000-0000-0000-0000-000000000005");
    private static readonly Guid ConversationId = Guid.Parse("20000000-0000-0000-0000-000000000005");
    private static readonly Guid ConversationTurnId = Guid.Parse("30000000-0000-0000-0000-000000000005");
    private static readonly Guid SessionId = Guid.Parse("40000000-0000-0000-0000-000000000005");
    private static readonly DateTimeOffset CanaryTime = new(2026, 8, 26, 0, 5, 0, TimeSpan.Zero);

    public static async Task<int> Main(string[] args)
    {
        var started = Stopwatch.StartNew();
        var phase = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "unknown";
        try
        {
            if (string.Equals(phase, "identity", StringComparison.Ordinal))
            {
                if (args.Length != 1)
                {
                    return WriteFailure(phase, "invalid_arguments", null, started.ElapsedMilliseconds, 2);
                }

                return WriteResult(new
                {
                    phase,
                    success = true,
                    errorCode = (string?)null,
                    exceptionType = (string?)null,
                    schemaVersion = V02Contract.SchemaVersion,
                    durationMilliseconds = started.ElapsedMilliseconds
                }, 0);
            }

            var arguments = ParseArguments(args.Skip(1));
            var databasePath = RequireArgument(arguments, "database");
            var ownedRoot = RequireArgument(arguments, "owned-root");
            var ownerToken = RequireArgument(arguments, "owner-token");
            ValidateOwnedDatabase(databasePath, ownedRoot, ownerToken);

            return phase switch
            {
                "create-v7" => await CreateVersion7Async(databasePath, phase, started),
                "read-canary" => await ReadCanaryAsync(databasePath, phase, started),
                "migrate-v8" => await MigrateVersion8Async(databasePath, phase, started),
                "inspect" => await InspectAsync(databasePath, phase, started),
                "inject-v8-conflict" => await InjectVersion8ConflictAsync(databasePath, phase, started),
                "migrate-v8-expect-failure" => await MigrateVersion8ExpectFailureAsync(databasePath, phase, started),
                "reject-v8" => await RejectVersion8Async(databasePath, phase, started),
                _ => WriteFailure(phase, "invalid_arguments", null, started.ElapsedMilliseconds, 2)
            };
        }
        catch (Exception exception)
        {
            return WriteFailure(
                phase,
                "runner_failed",
                exception.GetType().Name,
                started.ElapsedMilliseconds,
                1);
        }
    }

    private static async Task<int> CreateVersion7Async(
        string databasePath,
        string phase,
        Stopwatch started)
    {
#if !STAGE1_COMPATIBILITY
        return WriteFailure(phase, "stage1_runner_required", null, started.ElapsedMilliseconds, 2);
#else
        await using var taskStore = new SqliteTaskStore(databasePath);
        await taskStore.InitializeAsync();
        await taskStore.UpsertDeviceAsync(new DeviceRecord
        {
            Id = DeviceId,
            DisplayName = DeviceDisplayName,
            DeviceType = DeviceType.WindowsHost,
            TrustState = DeviceTrustState.Local,
            CreatedAtUtc = CanaryTime,
            LastSeenAtUtc = CanaryTime
        });

        var conversations = new SqliteConversationStore(databasePath);
        await conversations.InitializeAsync();
        await conversations.CreateConversationAsync(new ConversationRecord
        {
            Id = ConversationId,
            CreatedByDeviceId = DeviceId,
            Title = ConversationTitle,
            CreatedAtUtc = CanaryTime,
            UpdatedAtUtc = CanaryTime
        });
        var conversationTurn = await conversations.StartTurnAsync(
            ConversationId,
            ConversationTurnId,
            UserMessage,
            "stage1-conversation-canary",
            CanaryTime);
        await conversations.CompleteTurnAsync(
            ConversationId,
            conversationTurn.Turn.Id,
            AssistantMessage,
            "stage1-assistant-canary",
            CanaryTime.AddSeconds(1));

        await using var sessions = new SqliteSessionStore(databasePath);
        await sessions.InitializeAsync();
        await sessions.CreateSessionAsync(new SessionRecord
        {
            Id = SessionId,
            ConversationId = ConversationId,
            CreatedByDeviceId = DeviceId,
            Title = SessionTitle,
            IsCurrent = true,
            CreatedAtUtc = CanaryTime,
            UpdatedAtUtc = CanaryTime,
            LastActiveAtUtc = CanaryTime
        });
        var sessionTurn = await sessions.StartTurnAsync(
            SessionId,
            SessionInput,
            "Text",
            "stage1-session-canary",
            CanaryTime);
        await sessions.UpdateTurnAsync(
            sessionTurn.Turn with
            {
                WorkKind = SessionWorkKind.Conversation,
                Phase = SessionTurnPhase.Completed,
                ConversationTurnId = ConversationTurnId,
                ResultSummary = "Stage 1 compatibility completed",
                CompletedAtUtc = CanaryTime.AddSeconds(2)
            },
            sessionTurn.Turn.Version,
            CanaryTime.AddSeconds(2));

        var canaryReadable = await VerifyCanaryAsync(databasePath);
        return WriteResult(new
        {
            phase,
            success = canaryReadable,
            errorCode = canaryReadable ? null : "canary_unreadable",
            exceptionType = (string?)null,
            schemaVersion = await taskStore.GetSchemaVersionAsync(),
            canaryReadable,
            durationMilliseconds = started.ElapsedMilliseconds
        }, canaryReadable ? 0 : 1);
#endif
    }

    private static async Task<int> ReadCanaryAsync(
        string databasePath,
        string phase,
        Stopwatch started)
    {
        var canaryReadable = await VerifyCanaryAsync(databasePath);
        await using var taskStore = new SqliteTaskStore(databasePath);
        await taskStore.InitializeAsync();
        return WriteResult(new
        {
            phase,
            success = canaryReadable,
            errorCode = canaryReadable ? null : "canary_unreadable",
            exceptionType = (string?)null,
            schemaVersion = await taskStore.GetSchemaVersionAsync(),
            canaryReadable,
            durationMilliseconds = started.ElapsedMilliseconds
        }, canaryReadable ? 0 : 1);
    }

    private static async Task<int> MigrateVersion8Async(
        string databasePath,
        string phase,
        Stopwatch started)
    {
#if STAGE1_COMPATIBILITY
        return WriteFailure(phase, "current_runner_required", null, started.ElapsedMilliseconds, 2);
#else
        await using var taskStore = new SqliteTaskStore(databasePath);
        await taskStore.InitializeAsync();
        var canaryReadable = await VerifyCanaryAsync(databasePath);
        await using var sessions = new SqliteSessionStore(databasePath);
        await sessions.InitializeAsync();
        var historicalTurn = AssertSingle(await sessions.GetTurnsAsync(SessionId));
        var inspection = await InspectDatabaseAsync(databasePath);
        var success = canaryReadable &&
                      historicalTurn.FrozenRoute is null &&
                      inspection.SchemaVersion == 8 &&
                      inspection.IntegrityOk &&
                      inspection.HasAllVersion8Objects;
        return WriteResult(new
        {
            phase,
            success,
            errorCode = success ? null : "migration_verification_failed",
            exceptionType = (string?)null,
            schemaVersion = inspection.SchemaVersion,
            integrityOk = inspection.IntegrityOk,
            canaryReadable,
            historicalFrozenRouteNull = historicalTurn.FrozenRoute is null,
            hasAllVersion8Objects = inspection.HasAllVersion8Objects,
            durationMilliseconds = started.ElapsedMilliseconds
        }, success ? 0 : 1);
#endif
    }

    private static async Task<int> InspectAsync(
        string databasePath,
        string phase,
        Stopwatch started)
    {
        var inspection = await InspectDatabaseAsync(databasePath);
        var success = inspection.IntegrityOk;
        return WriteResult(new
        {
            phase,
            success,
            errorCode = success ? null : "integrity_check_failed",
            exceptionType = (string?)null,
            schemaVersion = inspection.SchemaVersion,
            integrityOk = inspection.IntegrityOk,
            hasAllVersion8Objects = inspection.HasAllVersion8Objects,
            hasAnyVersion8Objects = inspection.HasAnyVersion8Objects,
            schemaFingerprint = inspection.SchemaFingerprint,
            frozenRouteColumnCount = inspection.FrozenRouteColumnCount,
            aiInvocationTableCount = inspection.AiInvocationTableCount,
            aiInvocationIndexCount = inspection.AiInvocationIndexCount,
            durationMilliseconds = started.ElapsedMilliseconds
        }, success ? 0 : 1);
    }

    private static async Task<int> InjectVersion8ConflictAsync(
        string databasePath,
        string phase,
        Stopwatch started)
    {
#if STAGE1_COMPATIBILITY
        return WriteFailure(phase, "current_runner_required", null, started.ElapsedMilliseconds, 2);
#else
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ai_invocations (
                    id TEXT NOT NULL PRIMARY KEY,
                    conflict_canary TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var inspection = await InspectDatabaseAsync(databasePath);
        var success = inspection.SchemaVersion == 7 &&
                      inspection.IntegrityOk &&
                      inspection.AiInvocationTableCount == 1 &&
                      inspection.AiInvocationIndexCount == 0 &&
                      inspection.FrozenRouteColumnCount == 0;
        return WriteResult(new
        {
            phase,
            success,
            errorCode = success ? null : "conflict_fixture_invalid",
            exceptionType = (string?)null,
            schemaVersion = inspection.SchemaVersion,
            integrityOk = inspection.IntegrityOk,
            schemaFingerprint = inspection.SchemaFingerprint,
            frozenRouteColumnCount = inspection.FrozenRouteColumnCount,
            aiInvocationTableCount = inspection.AiInvocationTableCount,
            aiInvocationIndexCount = inspection.AiInvocationIndexCount,
            durationMilliseconds = started.ElapsedMilliseconds
        }, success ? 0 : 1);
#endif
    }

    private static async Task<int> MigrateVersion8ExpectFailureAsync(
        string databasePath,
        string phase,
        Stopwatch started)
    {
#if STAGE1_COMPATIBILITY
        return WriteFailure(phase, "current_runner_required", null, started.ElapsedMilliseconds, 2);
#else
        var migrationFailed = false;
        string? migrationExceptionType = null;
        await using (var taskStore = new SqliteTaskStore(databasePath))
        {
            try
            {
                await taskStore.InitializeAsync();
            }
            catch (Exception exception)
            {
                migrationFailed = true;
                migrationExceptionType = exception.GetType().Name;
            }
        }

        var inspection = await InspectDatabaseAsync(databasePath);
        var success = migrationFailed &&
                      inspection.SchemaVersion == 7 &&
                      inspection.IntegrityOk &&
                      inspection.AiInvocationTableCount == 1 &&
                      inspection.AiInvocationIndexCount == 0 &&
                      inspection.FrozenRouteColumnCount == 0;
        return WriteResult(new
        {
            phase,
            success,
            errorCode = success ? null : "migration_did_not_roll_back",
            exceptionType = migrationExceptionType,
            migrationFailed,
            schemaVersion = inspection.SchemaVersion,
            integrityOk = inspection.IntegrityOk,
            schemaFingerprint = inspection.SchemaFingerprint,
            frozenRouteColumnCount = inspection.FrozenRouteColumnCount,
            aiInvocationTableCount = inspection.AiInvocationTableCount,
            aiInvocationIndexCount = inspection.AiInvocationIndexCount,
            durationMilliseconds = started.ElapsedMilliseconds
        }, success ? 0 : 1);
#endif
    }

    private static async Task<int> RejectVersion8Async(
        string databasePath,
        string phase,
        Stopwatch started)
    {
#if !STAGE1_COMPATIBILITY
        return WriteFailure(phase, "stage1_runner_required", null, started.ElapsedMilliseconds, 2);
#else
        var beforeHash = ComputeFileHash(databasePath);
        var beforeInspection = await InspectDatabaseAsync(databasePath);
        var rejected = false;
        await using (var taskStore = new SqliteTaskStore(databasePath))
        {
            try
            {
                await taskStore.InitializeAsync();
            }
            catch (NotSupportedException)
            {
                rejected = true;
            }
        }

        var canaryReadable = await VerifyCanaryAsync(databasePath, initializeTaskStore: false);
        var afterInspection = await InspectDatabaseAsync(databasePath);
        var afterHash = ComputeFileHash(databasePath);
        var databaseHashUnchanged = string.Equals(beforeHash, afterHash, StringComparison.Ordinal);
        var schemaFingerprintUnchanged = string.Equals(
            beforeInspection.SchemaFingerprint,
            afterInspection.SchemaFingerprint,
            StringComparison.Ordinal);
        var success = rejected &&
                      beforeInspection.SchemaVersion == 8 &&
                      afterInspection.SchemaVersion == 8 &&
                      beforeInspection.IntegrityOk &&
                      afterInspection.IntegrityOk &&
                      databaseHashUnchanged &&
                      schemaFingerprintUnchanged &&
                      canaryReadable;
        return WriteResult(new
        {
            phase,
            success,
            errorCode = success ? null : "stage1_v8_rejection_invalid",
            exceptionType = (string?)null,
            stage1RejectedV8 = rejected,
            schemaVersion = afterInspection.SchemaVersion,
            databaseHashUnchanged,
            schemaFingerprintUnchanged,
            canaryUnchanged = canaryReadable,
            hostStarted = false,
            turnReplayAttempted = false,
            inPlaceDowngradeAttempted = false,
            durationMilliseconds = started.ElapsedMilliseconds
        }, success ? 0 : 1);
#endif
    }

    private static async Task<bool> VerifyCanaryAsync(
        string databasePath,
        bool initializeTaskStore = true)
    {
        await using var taskStore = new SqliteTaskStore(databasePath);
        if (initializeTaskStore)
        {
            await taskStore.InitializeAsync();
        }

        var device = await taskStore.GetDeviceAsync(DeviceId);

        var conversations = new SqliteConversationStore(databasePath);
        await conversations.InitializeAsync();
        var conversation = await conversations.GetConversationAsync(ConversationId);
        var messages = await conversations.GetMessagesAsync(ConversationId);
        var conversationTurns = await conversations.GetTurnsAsync(ConversationId);

        await using var sessions = new SqliteSessionStore(databasePath);
        await sessions.InitializeAsync();
        var session = await sessions.GetSessionAsync(SessionId);
        var sessionTurns = await sessions.GetTurnsAsync(SessionId);

        return device is { DisplayName: DeviceDisplayName } &&
               conversation is { Title: ConversationTitle, Status: ConversationStatus.Ready } &&
               messages.Count == 2 &&
               messages[0] is { Role: ConversationMessageRole.User, Content: UserMessage } &&
               messages[1] is { Role: ConversationMessageRole.Assistant, Content: AssistantMessage } &&
               conversationTurns.Count == 1 &&
               conversationTurns[0] is { Id: var turnId, Status: ConversationTurnStatus.Succeeded } &&
               turnId == ConversationTurnId &&
               session is { Title: SessionTitle, IsCurrent: true } &&
               sessionTurns.Count == 1 &&
               sessionTurns[0] is
               {
                   InputText: SessionInput,
                   WorkKind: SessionWorkKind.Conversation,
                   Phase: SessionTurnPhase.Completed,
                   ConversationTurnId: var linkedTurnId
               } &&
               linkedTurnId == ConversationTurnId;
    }

    private static async Task<DatabaseInspection> InspectDatabaseAsync(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var schemaVersion = await ExecuteScalarInt32Async(
            connection,
            "SELECT COALESCE(MAX(version), 0) FROM schema_info;");
        var integrity = await ExecuteScalarStringAsync(connection, "PRAGMA integrity_check;");
        var routeColumns = await ReadColumnNamesAsync(connection, "session_turns");
        var aiTableCount = await ExecuteScalarInt32Async(
            connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ai_invocations';");
        var aiIndexCount = await ExecuteScalarInt32Async(
            connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN (" +
            "'ix_ai_invocations_conversation_turn', 'ix_ai_invocations_session_turn', " +
            "'ix_ai_invocations_provider_model');");
        var requiredColumns = new[]
        {
            "frozen_route_status",
            "frozen_provider_id",
            "frozen_model_id",
            "frozen_data_destination",
            "frozen_sends_data_off_device",
            "frozen_at_utc",
            "frozen_route_failure_code"
        };
        var version8ColumnCount = requiredColumns.Count(routeColumns.Contains);
        var schemaFingerprint = await ReadSchemaFingerprintAsync(connection);
        return new DatabaseInspection(
            schemaVersion,
            string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase),
            aiTableCount == 1 &&
            aiIndexCount == 3 &&
            version8ColumnCount == requiredColumns.Length,
            aiTableCount > 0 || aiIndexCount > 0 || version8ColumnCount > 0,
            schemaFingerprint,
            version8ColumnCount,
            aiTableCount,
            aiIndexCount);
    }

    private static async Task<string> ReadSchemaFingerprintAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, name, COALESCE(sql, '')
            FROM sqlite_master
            WHERE name NOT LIKE 'sqlite_%'
            ORDER BY type, name;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var canonical = new StringBuilder();
        while (await reader.ReadAsync())
        {
            canonical.Append(reader.GetString(0));
            canonical.Append('|');
            canonical.Append(reader.GetString(1));
            canonical.Append('|');
            canonical.Append(reader.GetString(2));
            canonical.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static async Task<HashSet<string>> ReadColumnNamesAsync(
        SqliteConnection connection,
        string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task<int> ExecuteScalarInt32Async(
        SqliteConnection connection,
        string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<string> ExecuteScalarStringAsync(
        SqliteConnection connection,
        string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static T AssertSingle<T>(IReadOnlyList<T> items) =>
        items.Count == 1
            ? items[0]
            : throw new InvalidOperationException("Expected one canary record.");

    private static Dictionary<string, string> ParseArguments(IEnumerable<string> rawArguments)
    {
        var arguments = rawArguments.ToArray();
        if (arguments.Length % 2 != 0)
        {
            throw new ArgumentException("Arguments must be key/value pairs.");
        }

        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            var key = arguments[index];
            if (!key.StartsWith("--", StringComparison.Ordinal) ||
                !parsed.TryAdd(key[2..], arguments[index + 1]))
            {
                throw new ArgumentException("Arguments are invalid.");
            }
        }

        return parsed;
    }

    private static string RequireArgument(IReadOnlyDictionary<string, string> arguments, string key) =>
        arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("A required argument is missing.");

    private static void ValidateOwnedDatabase(string databasePath, string ownedRoot, string ownerToken)
    {
        var canonicalRoot = Path.GetFullPath(ownedRoot);
        var canonicalDatabase = Path.GetFullPath(databasePath);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var temporaryPrefix = temporaryRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var marker = Path.Combine(canonicalRoot, OwnerMarkerName);
        if (!canonicalRoot.StartsWith(temporaryPrefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(canonicalRoot).StartsWith(OwnedDirectoryPrefix, StringComparison.Ordinal) ||
            !canonicalDatabase.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(marker) ||
            !string.Equals(File.ReadAllText(marker), ownerToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Owned temporary database validation failed.");
        }
    }

    private static int WriteFailure(
        string phase,
        string errorCode,
        string? exceptionType,
        long durationMilliseconds,
        int exitCode) =>
        WriteResult(new
        {
            phase,
            success = false,
            errorCode,
            exceptionType,
            durationMilliseconds
        }, exitCode);

    private static int WriteResult(object result, int exitCode)
    {
        Console.WriteLine(ResultPrefix + JsonSerializer.Serialize(result));
        return exitCode;
    }

    private sealed record DatabaseInspection(
        int SchemaVersion,
        bool IntegrityOk,
        bool HasAllVersion8Objects,
        bool HasAnyVersion8Objects,
        string SchemaFingerprint,
        int FrozenRouteColumnCount,
        int AiInvocationTableCount,
        int AiInvocationIndexCount);
}
