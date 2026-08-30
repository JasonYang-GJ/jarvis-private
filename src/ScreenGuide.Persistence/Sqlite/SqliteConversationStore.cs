using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Memories;

namespace ScreenGuide.Persistence.Sqlite;

public sealed class SqliteConversationStore : IConversationStore
{
    private readonly string _connectionString;

    public SqliteConversationStore(string databasePath)
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
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'conversations';";
        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (count != 1)
        {
            throw new InvalidOperationException("对话数据表尚未完成初始化。");
        }
    }

    public async Task CreateConversationAsync(
        ConversationRecord conversation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversations(
                id, created_by_device_id, title, provider_id, external_thread_id,
                status, created_at_utc, updated_at_utc, last_message_at_utc,
                failure_code, failure_message, version)
            VALUES(
                $id, $deviceId, $title, $providerId, $externalThreadId,
                $status, $createdAtUtc, $updatedAtUtc, $lastMessageAtUtc,
                $failureCode, $failureMessage, $version);
            """;
        AddConversation(command, conversation);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<ConversationRecord?> GetConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default) =>
        QuerySingleAsync(
            "SELECT * FROM conversations WHERE id = $id;",
            conversationId,
            ReadConversation,
            cancellationToken);

    public async Task<IReadOnlyList<ConversationRecord>> GetConversationsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM conversations ORDER BY updated_at_utc DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ConversationRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadConversation(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<ConversationMessageRecord>> GetMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM conversation_messages
            WHERE conversation_id = $conversationId
            ORDER BY sequence_number;
            """;
        Add(command, "$conversationId", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ConversationMessageRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadMessage(reader));
        }

        return results;
    }

    public async Task<ConversationMessagePage> GetMessagesPageAsync(
        Guid conversationId,
        long? beforeSequenceNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM conversation_messages
            WHERE conversation_id = $conversationId
              AND ($beforeSequenceNumber IS NULL OR sequence_number < $beforeSequenceNumber)
            ORDER BY sequence_number DESC
            LIMIT $limit;
            """;
        Add(command, "$conversationId", conversationId);
        command.Parameters.AddWithValue(
            "$beforeSequenceNumber",
            beforeSequenceNumber is { } value ? value : DBNull.Value);
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        var descending = new List<ConversationMessageRecord>(pageSize + 1);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                descending.Add(ReadMessage(reader));
            }
        }

        var hasMore = descending.Count > pageSize;
        var items = descending.Take(pageSize).Reverse().ToArray();
        return new ConversationMessagePage(
            items,
            hasMore && items.Length > 0 ? items[0].SequenceNumber : null,
            hasMore);
    }

    public async Task<ConversationMessageChangeBatch> GetMessageChangesAsync(
        Guid conversationId,
        long afterSequenceNumber,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM conversation_messages
            WHERE conversation_id = $conversationId AND sequence_number > $afterSequenceNumber
            ORDER BY sequence_number
            LIMIT $limit;
            """;
        Add(command, "$conversationId", conversationId);
        command.Parameters.AddWithValue("$afterSequenceNumber", afterSequenceNumber);
        command.Parameters.AddWithValue("$limit", limit + 1);
        var values = new List<ConversationMessageRecord>(limit + 1);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                values.Add(ReadMessage(reader));
            }
        }

        var hasMore = values.Count > limit;
        var items = values.Take(limit).ToArray();
        return new ConversationMessageChangeBatch(
            items,
            items.Length == 0 ? afterSequenceNumber : items[^1].SequenceNumber,
            hasMore);
    }

    public async Task<IReadOnlyList<ConversationTurnRecord>> GetTurnsAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM conversation_turns
            WHERE conversation_id = $conversationId
            ORDER BY sequence_number;
            """;
        Add(command, "$conversationId", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ConversationTurnRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadTurn(reader));
        }

        return results;
    }

    public Task<ConversationTurnRegistration> StartTurnAsync(
        Guid conversationId,
        Guid turnId,
        string message,
        string idempotencyKey,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default) =>
        StartTurnCoreAsync(
            conversationId,
            turnId,
            message,
            idempotencyKey,
            memoryOutbound: null,
            startedAtUtc,
            cancellationToken);

    public Task<ConversationTurnRegistration> StartTurnWithMemoryAsync(
        Guid conversationId,
        Guid turnId,
        string message,
        string idempotencyKey,
        MemoryOutboundAuditMetadata memoryOutbound,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default) =>
        StartTurnCoreAsync(
            conversationId,
            turnId,
            message,
            idempotencyKey,
            memoryOutbound,
            startedAtUtc,
            cancellationToken);

    private async Task<ConversationTurnRegistration> StartTurnCoreAsync(
        Guid conversationId,
        Guid turnId,
        string message,
        string idempotencyKey,
        MemoryOutboundAuditMetadata? memoryOutbound,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("对话内容不能为空。", nameof(message));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var existing = await GetTurnByIdempotencyAsync(
            connection,
            transaction,
            conversationId,
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var existingMessage = await GetMessageInTransactionAsync(
                connection,
                transaction,
                existing.UserMessageId,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("重复对话 Turn 缺少用户消息。");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ConversationTurnRegistration(false, existing, existingMessage);
        }

        var conversation = await GetConversationInTransactionAsync(
            connection,
            transaction,
            conversationId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("对话不存在。");
        if (conversation.Status == ConversationStatus.Responding)
        {
            throw new InvalidOperationException("元枢仍在回答上一条消息。");
        }

        var messageSequence = await NextSequenceAsync(
            connection,
            transaction,
            "conversation_messages",
            conversationId,
            cancellationToken).ConfigureAwait(false);
        var turnSequence = checked((int)await NextSequenceAsync(
            connection,
            transaction,
            "conversation_turns",
            conversationId,
            cancellationToken).ConfigureAwait(false));
        var userMessage = new ConversationMessageRecord
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            SequenceNumber = messageSequence,
            Role = ConversationMessageRole.User,
            Content = message.Trim(),
            CreatedAtUtc = startedAtUtc
        };
        var turn = new ConversationTurnRecord
        {
            Id = turnId,
            ConversationId = conversationId,
            SequenceNumber = turnSequence,
            UserMessageId = userMessage.Id,
            IdempotencyKey = idempotencyKey,
            Status = ConversationTurnStatus.Running,
            StartedAtUtc = startedAtUtc,
            MemoryOutbound = memoryOutbound
        };
        await InsertMessageAsync(connection, transaction, userMessage, cancellationToken)
            .ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO conversation_turns(
                    id, conversation_id, sequence_number, user_message_id,
                    assistant_message_id, idempotency_key, status, process_id,
                    started_at_utc, completed_at_utc, failure_code, failure_message,
                    memory_derived, memory_consent_id, memory_origin_provider_id,
                    memory_origin_model_id, memory_origin_destination,
                    memory_item_refs_json, memory_item_count, memory_total_characters,
                    memory_manifest_hash)
                VALUES(
                    $id, $conversationId, $sequenceNumber, $userMessageId,
                    NULL, $idempotencyKey, $status, NULL,
                    $startedAtUtc, NULL, NULL, NULL,
                    0, $memoryConsentId, $memoryProviderId,
                    $memoryModelId, $memoryDestination,
                    $memoryItemRefsJson, $memoryItemCount, $memoryTotalCharacters,
                    $memoryManifestHash);
                """;
            Add(command, "$id", turn.Id);
            Add(command, "$conversationId", conversationId);
            command.Parameters.AddWithValue("$sequenceNumber", turn.SequenceNumber);
            Add(command, "$userMessageId", userMessage.Id);
            command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
            command.Parameters.AddWithValue("$status", turn.Status.ToString());
            command.Parameters.AddWithValue("$startedAtUtc", ToDb(startedAtUtc));
            AddMemoryOutbound(command, memoryOutbound);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE conversations
                SET title = CASE WHEN title = '新对话' THEN $firstMessageTitle ELSE title END,
                    status = $status,
                    updated_at_utc = $updatedAtUtc,
                    last_message_at_utc = $updatedAtUtc,
                    failure_code = NULL,
                    failure_message = NULL,
                    version = version + 1
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$status", ConversationStatus.Responding.ToString());
            command.Parameters.AddWithValue("$firstMessageTitle", BuildConversationTitle(message));
            command.Parameters.AddWithValue("$updatedAtUtc", ToDb(startedAtUtc));
            Add(command, "$id", conversationId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ConversationTurnRegistration(true, turn, userMessage);
    }

    public async Task RecordProviderStartedAsync(
        Guid conversationId,
        Guid turnId,
        string externalThreadId,
        int processId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE conversation_turns
                SET process_id = $processId
                WHERE id = $turnId AND conversation_id = $conversationId AND status = 'Running';
                """;
            command.Parameters.AddWithValue("$processId", processId);
            Add(command, "$turnId", turnId);
            Add(command, "$conversationId", conversationId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE conversations
                SET external_thread_id = $externalThreadId,
                    updated_at_utc = $updatedAtUtc,
                    version = version + 1
                WHERE id = $conversationId;
                """;
            command.Parameters.AddWithValue("$externalThreadId", externalThreadId);
            command.Parameters.AddWithValue("$updatedAtUtc", ToDb(startedAtUtc));
            Add(command, "$conversationId", conversationId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteTurnAsync(
        Guid conversationId,
        Guid turnId,
        string reply,
        string? providerMessageId,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            throw new InvalidDataException("对话 Provider 没有返回可显示的回答。");
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var turn = await GetTurnInTransactionAsync(
            connection,
            transaction,
            turnId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("对话 Turn 不存在。");
        if (turn.Status == ConversationTurnStatus.Succeeded)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (turn.Status != ConversationTurnStatus.Running)
        {
            throw new InvalidOperationException("当前对话 Turn 已经结束。");
        }

        var assistant = new ConversationMessageRecord
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            SequenceNumber = await NextSequenceAsync(
                connection,
                transaction,
                "conversation_messages",
                conversationId,
                cancellationToken).ConfigureAwait(false),
            Role = ConversationMessageRole.Assistant,
            Content = reply.Trim(),
            CreatedAtUtc = completedAtUtc,
            ProviderMessageId = providerMessageId
        };
        await InsertMessageAsync(connection, transaction, assistant, cancellationToken)
            .ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE conversation_turns
                SET assistant_message_id = $assistantMessageId,
                    status = $status,
                    memory_derived = CASE WHEN memory_consent_id IS NULL THEN 0 ELSE 1 END,
                    completed_at_utc = $completedAtUtc,
                    failure_code = NULL,
                    failure_message = NULL
                WHERE id = $turnId AND conversation_id = $conversationId;
                """;
            Add(command, "$assistantMessageId", assistant.Id);
            command.Parameters.AddWithValue("$status", ConversationTurnStatus.Succeeded.ToString());
            command.Parameters.AddWithValue("$completedAtUtc", ToDb(completedAtUtc));
            Add(command, "$turnId", turnId);
            Add(command, "$conversationId", conversationId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE conversations
                SET status = $status,
                    updated_at_utc = $updatedAtUtc,
                    last_message_at_utc = $updatedAtUtc,
                    failure_code = NULL,
                    failure_message = NULL,
                    version = version + 1
                WHERE id = $conversationId;
                """;
            command.Parameters.AddWithValue("$status", ConversationStatus.Ready.ToString());
            command.Parameters.AddWithValue("$updatedAtUtc", ToDb(completedAtUtc));
            Add(command, "$conversationId", conversationId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FailTurnAsync(
        Guid conversationId,
        Guid turnId,
        ConversationTurnStatus status,
        string failureCode,
        string failureMessage,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (status is ConversationTurnStatus.Running or ConversationTurnStatus.Succeeded)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        var conversationStatus = status switch
        {
            ConversationTurnStatus.Cancelled => ConversationStatus.Ready,
            ConversationTurnStatus.Interrupted => ConversationStatus.Interrupted,
            _ => ConversationStatus.Failed
        };
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var turnChanged = false;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE conversation_turns
                SET status = $status,
                    completed_at_utc = $completedAtUtc,
                    failure_code = $failureCode,
                    failure_message = $failureMessage
                WHERE id = $turnId AND conversation_id = $conversationId AND status = 'Running';
                """;
            command.Parameters.AddWithValue("$status", status.ToString());
            command.Parameters.AddWithValue("$completedAtUtc", ToDb(completedAtUtc));
            command.Parameters.AddWithValue("$failureCode", failureCode);
            command.Parameters.AddWithValue("$failureMessage", failureMessage);
            Add(command, "$turnId", turnId);
            Add(command, "$conversationId", conversationId);
            turnChanged = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }

        if (turnChanged)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE conversations
                SET status = $status,
                    updated_at_utc = $updatedAtUtc,
                    failure_code = $failureCode,
                    failure_message = $failureMessage,
                    version = version + 1
                WHERE id = $conversationId;
                """;
            command.Parameters.AddWithValue("$status", conversationStatus.ToString());
            command.Parameters.AddWithValue("$updatedAtUtc", ToDb(completedAtUtc));
            command.Parameters.AddWithValue("$failureCode", failureCode);
            command.Parameters.AddWithValue("$failureMessage", failureMessage);
            Add(command, "$conversationId", conversationId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConversationRecoveryResult> RecoverInterruptedAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var ids = new List<Guid>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id FROM conversations WHERE status = 'Responding';";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        foreach (var id in ids)
        {
            await using var turnCommand = connection.CreateCommand();
            turnCommand.Transaction = transaction;
            turnCommand.CommandText = """
                UPDATE conversation_turns
                SET status = 'Interrupted',
                    completed_at_utc = $recoveredAtUtc,
                    failure_code = 'host_restarted',
                    failure_message = 'Desktop Host 重启，对话回答已中断。'
                WHERE conversation_id = $conversationId AND status = 'Running';
                """;
            turnCommand.Parameters.AddWithValue("$recoveredAtUtc", ToDb(recoveredAtUtc));
            Add(turnCommand, "$conversationId", id);
            await turnCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var conversationCommand = connection.CreateCommand();
            conversationCommand.Transaction = transaction;
            conversationCommand.CommandText = """
                UPDATE conversations
                SET status = 'Interrupted',
                    updated_at_utc = $recoveredAtUtc,
                    failure_code = 'host_restarted',
                    failure_message = '上一次回答因 Desktop Host 重启而中断。',
                    version = version + 1
                WHERE id = $conversationId;
                """;
            conversationCommand.Parameters.AddWithValue("$recoveredAtUtc", ToDb(recoveredAtUtc));
            Add(conversationCommand, "$conversationId", id);
            await conversationCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ConversationRecoveryResult(ids);
    }

    private Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) =>
        SqliteConnectionOpener.OpenAsync(_connectionString, cancellationToken);

    private async Task<T?> QuerySingleAsync<T>(
        string sql,
        Guid id,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken)
        where T : class
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? map(reader) : null;
    }

    private static async Task<ConversationRecord?> GetConversationInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM conversations WHERE id = $id;";
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadConversation(reader)
            : null;
    }

    private static async Task<ConversationMessageRecord?> GetMessageInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM conversation_messages WHERE id = $id;";
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMessage(reader) : null;
    }

    private static async Task<ConversationTurnRecord?> GetTurnInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM conversation_turns WHERE id = $id;";
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadTurn(reader) : null;
    }

    private static async Task<ConversationTurnRecord?> GetTurnByIdempotencyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT * FROM conversation_turns
            WHERE conversation_id = $conversationId AND idempotency_key = $idempotencyKey;
            """;
        Add(command, "$conversationId", conversationId);
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadTurn(reader) : null;
    }

    private static async Task<long> NextSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        if (table is not ("conversation_messages" or "conversation_turns"))
        {
            throw new ArgumentOutOfRangeException(nameof(table));
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COALESCE(MAX(sequence_number), 0) + 1 FROM {table} WHERE conversation_id = $conversationId;";
        Add(command, "$conversationId", conversationId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task InsertMessageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConversationMessageRecord message,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversation_messages(
                id, conversation_id, sequence_number, role, content,
                created_at_utc, provider_message_id)
            VALUES(
                $id, $conversationId, $sequenceNumber, $role, $content,
                $createdAtUtc, $providerMessageId);
            """;
        Add(command, "$id", message.Id);
        Add(command, "$conversationId", message.ConversationId);
        command.Parameters.AddWithValue("$sequenceNumber", message.SequenceNumber);
        command.Parameters.AddWithValue("$role", message.Role.ToString());
        command.Parameters.AddWithValue("$content", message.Content);
        command.Parameters.AddWithValue("$createdAtUtc", ToDb(message.CreatedAtUtc));
        command.Parameters.AddWithValue(
            "$providerMessageId",
            (object?)message.ProviderMessageId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddConversation(SqliteCommand command, ConversationRecord value)
    {
        Add(command, "$id", value.Id);
        Add(command, "$deviceId", value.CreatedByDeviceId);
        command.Parameters.AddWithValue("$title", value.Title);
        command.Parameters.AddWithValue("$providerId", value.ProviderId);
        command.Parameters.AddWithValue("$externalThreadId", (object?)value.ExternalThreadId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", value.Status.ToString());
        command.Parameters.AddWithValue("$createdAtUtc", ToDb(value.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedAtUtc", ToDb(value.UpdatedAtUtc));
        command.Parameters.AddWithValue("$lastMessageAtUtc", ToDbOrNull(value.LastMessageAtUtc));
        command.Parameters.AddWithValue("$failureCode", (object?)value.FailureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$failureMessage", (object?)value.FailureMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$version", value.Version);
    }

    private static ConversationRecord ReadConversation(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        CreatedByDeviceId = ReadGuid(reader, "created_by_device_id"),
        Title = reader.GetString(reader.GetOrdinal("title")),
        ProviderId = reader.GetString(reader.GetOrdinal("provider_id")),
        ExternalThreadId = ReadNullableString(reader, "external_thread_id"),
        Status = Enum.Parse<ConversationStatus>(reader.GetString(reader.GetOrdinal("status"))),
        CreatedAtUtc = ReadDate(reader, "created_at_utc"),
        UpdatedAtUtc = ReadDate(reader, "updated_at_utc"),
        LastMessageAtUtc = ReadNullableDate(reader, "last_message_at_utc"),
        FailureCode = ReadNullableString(reader, "failure_code"),
        FailureMessage = ReadNullableString(reader, "failure_message"),
        Version = reader.GetInt64(reader.GetOrdinal("version"))
    };

    private static ConversationMessageRecord ReadMessage(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        ConversationId = ReadGuid(reader, "conversation_id"),
        SequenceNumber = reader.GetInt64(reader.GetOrdinal("sequence_number")),
        Role = Enum.Parse<ConversationMessageRole>(reader.GetString(reader.GetOrdinal("role"))),
        Content = reader.GetString(reader.GetOrdinal("content")),
        CreatedAtUtc = ReadDate(reader, "created_at_utc"),
        ProviderMessageId = ReadNullableString(reader, "provider_message_id")
    };

    private static ConversationTurnRecord ReadTurn(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        ConversationId = ReadGuid(reader, "conversation_id"),
        SequenceNumber = reader.GetInt32(reader.GetOrdinal("sequence_number")),
        UserMessageId = ReadGuid(reader, "user_message_id"),
        AssistantMessageId = ReadNullableGuid(reader, "assistant_message_id"),
        IdempotencyKey = reader.GetString(reader.GetOrdinal("idempotency_key")),
        Status = Enum.Parse<ConversationTurnStatus>(reader.GetString(reader.GetOrdinal("status"))),
        ProcessId = ReadNullableInt(reader, "process_id"),
        StartedAtUtc = ReadDate(reader, "started_at_utc"),
        CompletedAtUtc = ReadNullableDate(reader, "completed_at_utc"),
        FailureCode = ReadNullableString(reader, "failure_code"),
        FailureMessage = ReadNullableString(reader, "failure_message"),
        MemoryDerived = reader.GetInt32(reader.GetOrdinal("memory_derived")) == 1,
        MemoryOutbound = ReadMemoryOutbound(reader)
    };

    private static MemoryOutboundAuditMetadata? ReadMemoryOutbound(SqliteDataReader reader)
    {
        var consentId = ReadNullableGuid(reader, "memory_consent_id");
        if (consentId is null)
        {
            return null;
        }

        var references = JsonSerializer.Deserialize<MemoryOutboundItemReference[]>(
            ReadNullableString(reader, "memory_item_refs_json") ?? "[]") ?? [];
        return new MemoryOutboundAuditMetadata(
            consentId.Value,
            DateTimeOffset.MinValue,
            DateTimeOffset.MinValue,
            null,
            ReadRequiredString(reader, "memory_origin_provider_id"),
            ReadRequiredString(reader, "memory_origin_model_id"),
            ReadRequiredString(reader, "memory_origin_destination"),
            string.Empty,
            string.Empty,
            string.Empty,
            null,
            references,
            reader.GetInt32(reader.GetOrdinal("memory_item_count")),
            reader.GetInt32(reader.GetOrdinal("memory_total_characters")),
            ReadRequiredString(reader, "memory_manifest_hash"));
    }

    private static void AddMemoryOutbound(
        SqliteCommand command,
        MemoryOutboundAuditMetadata? metadata)
    {
        command.Parameters.AddWithValue(
            "$memoryConsentId",
            metadata?.ConsentId.ToString("D") as object ?? DBNull.Value);
        command.Parameters.AddWithValue("$memoryProviderId", metadata?.ProviderId as object ?? DBNull.Value);
        command.Parameters.AddWithValue("$memoryModelId", metadata?.ModelId as object ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$memoryDestination",
            metadata?.DestinationOrigin as object ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$memoryItemRefsJson",
            metadata is null ? DBNull.Value : JsonSerializer.Serialize(metadata.Items));
        command.Parameters.AddWithValue("$memoryItemCount", (object?)metadata?.ItemCount ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$memoryTotalCharacters",
            (object?)metadata?.TotalCharacters ?? DBNull.Value);
        command.Parameters.AddWithValue("$memoryManifestHash", metadata?.ManifestHash as object ?? DBNull.Value);
    }

    private static string ReadRequiredString(SqliteDataReader reader, string name) =>
        ReadNullableString(reader, name)
        ?? throw new InvalidDataException($"记忆出站元数据缺少 {name}。");

    private static void Add(SqliteCommand command, string name, Guid value) =>
        command.Parameters.AddWithValue(name, value.ToString("D"));

    private static Guid ReadGuid(SqliteDataReader reader, string name) =>
        Guid.Parse(reader.GetString(reader.GetOrdinal(name)));

    private static Guid? ReadNullableGuid(SqliteDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name))
            ? null
            : Guid.Parse(reader.GetString(reader.GetOrdinal(name)));

    private static int? ReadNullableInt(SqliteDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetInt32(reader.GetOrdinal(name));

    private static string? ReadNullableString(SqliteDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));

    private static string ToDb(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private static string BuildConversationTitle(string message)
    {
        var firstLine = message.ReplaceLineEndings(" ").Trim();
        return firstLine.Length <= 28 ? firstLine : firstLine[..28] + "…";
    }

    private static object ToDbOrNull(DateTimeOffset? value) =>
        value is null ? DBNull.Value : ToDb(value.Value);

    private static DateTimeOffset ReadDate(SqliteDataReader reader, string name) =>
        DateTimeOffset.Parse(
            reader.GetString(reader.GetOrdinal(name)),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : ReadDate(reader, name);
}
