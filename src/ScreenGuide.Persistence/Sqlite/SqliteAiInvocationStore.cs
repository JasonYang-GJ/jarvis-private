using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScreenGuide.Core.Ai;
using ScreenGuide.Core.Memories;

namespace ScreenGuide.Persistence.Sqlite;

public sealed class SqliteAiInvocationStore : IAiInvocationStore
{
    private readonly string _connectionString;

    public SqliteAiInvocationStore(string databasePath)
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
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ai_invocations';";
        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (count != 1)
        {
            throw new InvalidOperationException("AI 调用追踪表尚未完成初始化。");
        }
    }

    public async Task StartAsync(
        AiInvocationRecord invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (invocation.Status != AiInvocationStatus.Running)
        {
            throw new ArgumentException("新的 AI 调用必须处于 Running 状态。", nameof(invocation));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ai_invocations(
                id, session_turn_id, conversation_turn_id, purpose, provider_id, model_id,
                prompt_id, prompt_version, prompt_content_hash, data_destination, status,
                started_at_utc, completed_at_utc, finish_reason, input_tokens, output_tokens,
                total_tokens, provider_request_id, failure_code,
                memory_consent_id, memory_origin_provider_id, memory_origin_model_id,
                memory_origin_destination, memory_item_refs_json, memory_item_count,
                memory_total_characters, memory_manifest_hash)
            VALUES(
                $id, $sessionTurnId, $conversationTurnId, $purpose, $providerId, $modelId,
                $promptId, $promptVersion, $promptHash, $destination, $status,
                $startedAtUtc, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
                $memoryConsentId, $memoryProviderId, $memoryModelId,
                $memoryDestination, $memoryItemRefsJson, $memoryItemCount,
                $memoryTotalCharacters, $memoryManifestHash);
            """;
        command.Parameters.AddWithValue("$id", invocation.Id.ToString("D"));
        command.Parameters.AddWithValue("$sessionTurnId", DbGuid(invocation.SessionTurnId));
        command.Parameters.AddWithValue("$conversationTurnId", DbGuid(invocation.ConversationTurnId));
        command.Parameters.AddWithValue("$purpose", invocation.Purpose.ToString());
        command.Parameters.AddWithValue("$providerId", Required(invocation.ProviderId, nameof(invocation.ProviderId)));
        command.Parameters.AddWithValue("$modelId", Required(invocation.ModelId, nameof(invocation.ModelId)));
        command.Parameters.AddWithValue("$promptId", Required(invocation.PromptId, nameof(invocation.PromptId)));
        command.Parameters.AddWithValue(
            "$promptVersion",
            Required(invocation.PromptVersion, nameof(invocation.PromptVersion)));
        command.Parameters.AddWithValue("$promptHash", Required(invocation.PromptContentHash, nameof(invocation.PromptContentHash)));
        command.Parameters.AddWithValue("$destination", Required(invocation.DataDestination, nameof(invocation.DataDestination)));
        command.Parameters.AddWithValue("$status", invocation.Status.ToString());
        command.Parameters.AddWithValue("$startedAtUtc", invocation.StartedAtUtc.ToString("O"));
        AddMemoryOutbound(command, invocation.MemoryOutbound);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<AiInvocationTransitionResult> CompleteAsync(
        Guid invocationId,
        string finishReason,
        AiTokenUsage? usage,
        string? providerRequestId,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default) =>
        EndAsync(
            invocationId,
            AiInvocationStatus.Succeeded,
            Required(finishReason, nameof(finishReason)),
            usage,
            providerRequestId,
            null,
            completedAtUtc,
            cancellationToken);

    public Task<AiInvocationTransitionResult> FailAsync(
        Guid invocationId,
        AiInvocationStatus status,
        string failureCode,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (status is AiInvocationStatus.Running or AiInvocationStatus.Succeeded)
        {
            throw new ArgumentOutOfRangeException(nameof(status), "失败终态无效。");
        }

        return EndAsync(
            invocationId,
            status,
            null,
            null,
            null,
            Required(failureCode, nameof(failureCode)),
            completedAtUtc,
            cancellationToken);
    }

    public async Task<AiInvocationRecoveryResult> InterruptRunningAsync(
        DateTimeOffset interruptedAtUtc,
        string failureCode,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ai_invocations
            SET status = 'Interrupted',
                completed_at_utc = $interruptedAtUtc,
                finish_reason = NULL,
                input_tokens = NULL,
                output_tokens = NULL,
                total_tokens = NULL,
                provider_request_id = NULL,
                failure_code = $failureCode
            WHERE status = 'Running'
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$interruptedAtUtc", interruptedAtUtc.ToString("O"));
        command.Parameters.AddWithValue(
            "$failureCode",
            Required(failureCode, nameof(failureCode)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var interruptedIds = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            interruptedIds.Add(Guid.Parse(reader.GetString(0)));
        }

        return new AiInvocationRecoveryResult(interruptedIds);
    }

    public async Task<AiInvocationRecord?> GetAsync(
        Guid invocationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ai_invocations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", invocationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<AiInvocationRecord>> GetForConversationTurnAsync(
        Guid conversationTurnId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM ai_invocations
            WHERE conversation_turn_id = $conversationTurnId
            ORDER BY started_at_utc, id;
            """;
        command.Parameters.AddWithValue("$conversationTurnId", conversationTurnId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<AiInvocationRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Read(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<AiInvocationRecord>> GetForSessionTurnAsync(
        Guid sessionTurnId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM ai_invocations
            WHERE session_turn_id = $sessionTurnId
            ORDER BY started_at_utc, id;
            """;
        command.Parameters.AddWithValue("$sessionTurnId", sessionTurnId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<AiInvocationRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Read(reader));
        }

        return results;
    }

    private async Task<AiInvocationTransitionResult> EndAsync(
        Guid invocationId,
        AiInvocationStatus status,
        string? finishReason,
        AiTokenUsage? usage,
        string? providerRequestId,
        string? failureCode,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ai_invocations
            SET status = $status,
                completed_at_utc = $completedAtUtc,
                finish_reason = $finishReason,
                input_tokens = $inputTokens,
                output_tokens = $outputTokens,
                total_tokens = $totalTokens,
                provider_request_id = $providerRequestId,
                failure_code = $failureCode
            WHERE id = $id AND status = 'Running';
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$completedAtUtc", completedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$finishReason", DbText(finishReason));
        command.Parameters.AddWithValue("$inputTokens", usage is null ? DBNull.Value : usage.InputTokens);
        command.Parameters.AddWithValue("$outputTokens", usage is null ? DBNull.Value : usage.OutputTokens);
        command.Parameters.AddWithValue("$totalTokens", usage is null ? DBNull.Value : usage.TotalTokens);
        command.Parameters.AddWithValue("$providerRequestId", DbText(providerRequestId));
        command.Parameters.AddWithValue("$failureCode", DbText(failureCode));
        command.Parameters.AddWithValue("$id", invocationId.ToString("D"));
        var applied = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        var current = await GetAsync(connection, invocationId, cancellationToken).ConfigureAwait(false)
            ?? throw new AiInvocationNotFoundException(invocationId);
        var disposition = applied
            ? AiInvocationTransitionDisposition.Applied
            : current.Status == status
                ? AiInvocationTransitionDisposition.AlreadyInRequestedTerminal
                : AiInvocationTransitionDisposition.RejectedByExistingTerminal;
        return new AiInvocationTransitionResult(status, disposition, current);
    }

    private static async Task<AiInvocationRecord?> GetAsync(
        SqliteConnection connection,
        Guid invocationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ai_invocations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", invocationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return Read(reader);
    }

    private Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) =>
        SqliteConnectionOpener.OpenAsync(_connectionString, cancellationToken);

    private static AiInvocationRecord Read(SqliteDataReader reader)
    {
        var input = NullableLong(reader, "input_tokens");
        var output = NullableLong(reader, "output_tokens");
        var total = NullableLong(reader, "total_tokens");
        return new AiInvocationRecord
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
            SessionTurnId = NullableGuid(reader, "session_turn_id"),
            ConversationTurnId = NullableGuid(reader, "conversation_turn_id"),
            Purpose = Enum.Parse<AiInvocationPurpose>(reader.GetString(reader.GetOrdinal("purpose"))),
            ProviderId = reader.GetString(reader.GetOrdinal("provider_id")),
            ModelId = reader.GetString(reader.GetOrdinal("model_id")),
            PromptId = reader.GetString(reader.GetOrdinal("prompt_id")),
            PromptVersion = reader.GetString(reader.GetOrdinal("prompt_version")),
            PromptContentHash = reader.GetString(reader.GetOrdinal("prompt_content_hash")),
            DataDestination = reader.GetString(reader.GetOrdinal("data_destination")),
            Status = Enum.Parse<AiInvocationStatus>(reader.GetString(reader.GetOrdinal("status"))),
            StartedAtUtc = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("started_at_utc")), CultureInfo.InvariantCulture),
            CompletedAtUtc = NullableDate(reader, "completed_at_utc"),
            FinishReason = NullableText(reader, "finish_reason"),
            Usage = input is null || output is null || total is null
                ? null
                : new AiTokenUsage(input.Value, output.Value, total.Value),
            ProviderRequestId = NullableText(reader, "provider_request_id"),
            FailureCode = NullableText(reader, "failure_code"),
            MemoryOutbound = ReadMemoryOutbound(reader)
        };
    }

    private static MemoryOutboundAuditMetadata? ReadMemoryOutbound(SqliteDataReader reader)
    {
        var consentId = NullableGuid(reader, "memory_consent_id");
        if (consentId is null)
        {
            return null;
        }

        var references = JsonSerializer.Deserialize<MemoryOutboundItemReference[]>(
            NullableText(reader, "memory_item_refs_json") ?? "[]") ?? [];
        return new MemoryOutboundAuditMetadata(
            consentId.Value,
            DateTimeOffset.MinValue,
            DateTimeOffset.MinValue,
            null,
            Required(NullableText(reader, "memory_origin_provider_id"), "memory_origin_provider_id"),
            Required(NullableText(reader, "memory_origin_model_id"), "memory_origin_model_id"),
            Required(NullableText(reader, "memory_origin_destination"), "memory_origin_destination"),
            Required(reader.GetString(reader.GetOrdinal("prompt_id")), "prompt_id"),
            Required(reader.GetString(reader.GetOrdinal("prompt_version")), "prompt_version"),
            Required(reader.GetString(reader.GetOrdinal("prompt_content_hash")), "prompt_content_hash"),
            null,
            references,
            reader.GetInt32(reader.GetOrdinal("memory_item_count")),
            reader.GetInt32(reader.GetOrdinal("memory_total_characters")),
            Required(NullableText(reader, "memory_manifest_hash"), "memory_manifest_hash"));
    }

    private static void AddMemoryOutbound(
        SqliteCommand command,
        MemoryOutboundAuditMetadata? metadata)
    {
        command.Parameters.AddWithValue("$memoryConsentId", DbGuid(metadata?.ConsentId));
        command.Parameters.AddWithValue("$memoryProviderId", DbText(metadata?.ProviderId));
        command.Parameters.AddWithValue("$memoryModelId", DbText(metadata?.ModelId));
        command.Parameters.AddWithValue("$memoryDestination", DbText(metadata?.DestinationOrigin));
        command.Parameters.AddWithValue(
            "$memoryItemRefsJson",
            metadata is null ? DBNull.Value : JsonSerializer.Serialize(metadata.Items));
        command.Parameters.AddWithValue("$memoryItemCount", (object?)metadata?.ItemCount ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$memoryTotalCharacters",
            (object?)metadata?.TotalCharacters ?? DBNull.Value);
        command.Parameters.AddWithValue("$memoryManifestHash", DbText(metadata?.ManifestHash));
    }

    private static object DbGuid(Guid? value) => value is null ? DBNull.Value : value.Value.ToString("D");

    private static object DbText(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static string Required(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("值不能为空。", parameterName)
            : value.Trim();

    private static string? NullableText(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static Guid? NullableGuid(SqliteDataReader reader, string name) =>
        NullableText(reader, name) is { } value ? Guid.Parse(value) : null;

    private static long? NullableLong(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static DateTimeOffset? NullableDate(SqliteDataReader reader, string name) =>
        NullableText(reader, name) is { } value
            ? DateTimeOffset.Parse(value, CultureInfo.InvariantCulture)
            : null;
}
