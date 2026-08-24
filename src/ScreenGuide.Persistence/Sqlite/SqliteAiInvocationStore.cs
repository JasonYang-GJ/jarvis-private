using System.Globalization;
using Microsoft.Data.Sqlite;
using ScreenGuide.Core.Ai;

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
                total_tokens, provider_request_id, failure_code)
            VALUES(
                $id, $sessionTurnId, $conversationTurnId, $purpose, $providerId, $modelId,
                $promptId, $promptVersion, $promptHash, $destination, $status,
                $startedAtUtc, NULL, NULL, NULL, NULL, NULL, NULL, NULL);
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
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task CompleteAsync(
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

    public Task FailAsync(
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

    private async Task EndAsync(
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
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("AI 调用不存在或已经进入终态。");
        }
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
            FailureCode = NullableText(reader, "failure_code")
        };
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
