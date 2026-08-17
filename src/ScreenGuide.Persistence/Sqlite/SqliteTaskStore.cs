using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScreenGuide.Core.Tasking;
using TaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

namespace ScreenGuide.Persistence.Sqlite;

public sealed class SqliteTaskStore : ILocalTaskStore
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public SqliteTaskStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("数据库路径不能为空。", nameof(databasePath));
        }

        _databasePath = Path.GetFullPath(databasePath.Trim());
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var parent = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_info (
                version INTEGER NOT NULL PRIMARY KEY,
                applied_at_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_info;";
        var storedVersion = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (storedVersion > V01Contract.SchemaVersion)
        {
            throw new NotSupportedException(
                $"数据库版本 {storedVersion} 高于当前支持的版本 {V01Contract.SchemaVersion}。");
        }

        command.CommandText = SqliteSchema.CreateVersion1;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.CommandText = """
            INSERT OR IGNORE INTO schema_info(version, applied_at_utc)
            VALUES ($version, $appliedAtUtc);
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$version", V01Contract.SchemaVersion);
        command.Parameters.AddWithValue("$appliedAtUtc", ToDb(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertDeviceAsync(
        DeviceRecord device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        RequireText(device.DisplayName, nameof(device.DisplayName));

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO devices(
                id, display_name, device_type, trust_state, public_key_thumbprint,
                created_at_utc, last_seen_at_utc, revoked_at_utc)
            VALUES(
                $id, $displayName, $deviceType, $trustState, $publicKeyThumbprint,
                $createdAtUtc, $lastSeenAtUtc, $revokedAtUtc)
            ON CONFLICT(id) DO UPDATE SET
                display_name = excluded.display_name,
                device_type = excluded.device_type,
                trust_state = excluded.trust_state,
                public_key_thumbprint = excluded.public_key_thumbprint,
                last_seen_at_utc = excluded.last_seen_at_utc,
                revoked_at_utc = excluded.revoked_at_utc;
            """;
        Add(command, "$id", device.Id);
        command.Parameters.AddWithValue("$displayName", device.DisplayName.Trim());
        command.Parameters.AddWithValue("$deviceType", device.DeviceType.ToString());
        command.Parameters.AddWithValue("$trustState", device.TrustState.ToString());
        AddNullable(command, "$publicKeyThumbprint", device.PublicKeyThumbprint);
        Add(command, "$createdAtUtc", device.CreatedAtUtc);
        AddNullable(command, "$lastSeenAtUtc", device.LastSeenAtUtc);
        AddNullable(command, "$revokedAtUtc", device.RevokedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetProjectAuthorizationAsync(
        ProjectRecord project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        RequireText(project.Name, nameof(project.Name));
        if (project.AuthorizationState == ProjectAuthorizationState.Authorized && project.RevokedAtUtc is not null)
        {
            throw new ArgumentException("已授权项目不能设置撤销时间。", nameof(project));
        }

        if (project.AuthorizationState == ProjectAuthorizationState.Revoked && project.RevokedAtUtc is null)
        {
            throw new ArgumentException("撤销项目必须记录撤销时间。", nameof(project));
        }

        var normalizedRoot = ProjectPathPolicy.NormalizeExistingRoot(project.RootPath);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO projects(
                id, name, root_path, authorization_state, authorized_by_device_id,
                authorized_at_utc, revoked_at_utc, created_at_utc, updated_at_utc)
            VALUES(
                $id, $name, $rootPath, $authorizationState, $authorizedByDeviceId,
                $authorizedAtUtc, $revokedAtUtc, $createdAtUtc, $updatedAtUtc)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                root_path = excluded.root_path,
                authorization_state = excluded.authorization_state,
                authorized_by_device_id = excluded.authorized_by_device_id,
                authorized_at_utc = excluded.authorized_at_utc,
                revoked_at_utc = excluded.revoked_at_utc,
                updated_at_utc = excluded.updated_at_utc;
            """;
        Add(command, "$id", project.Id);
        command.Parameters.AddWithValue("$name", project.Name.Trim());
        command.Parameters.AddWithValue("$rootPath", normalizedRoot);
        command.Parameters.AddWithValue("$authorizationState", project.AuthorizationState.ToString());
        Add(command, "$authorizedByDeviceId", project.AuthorizedByDeviceId);
        Add(command, "$authorizedAtUtc", project.AuthorizedAtUtc);
        AddNullable(command, "$revokedAtUtc", project.RevokedAtUtc);
        Add(command, "$createdAtUtc", project.CreatedAtUtc);
        Add(command, "$updatedAtUtc", project.UpdatedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await InsertAuditAsync(
            connection,
            transaction,
            project.AuthorizedAtUtc,
            project.AuthorizedByDeviceId,
            project.AuthorizationState == ProjectAuthorizationState.Authorized
                ? "ProjectAuthorized"
                : "ProjectRevoked",
            "Project",
            project.Id.ToString("D"),
            AuditOutcome.Success,
            JsonSerializer.Serialize(new { rootPath = normalizedRoot }),
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    public Task<DeviceRecord?> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
        QuerySingleAsync(
            "SELECT * FROM devices WHERE id = $id;",
            deviceId,
            ReadDevice,
            cancellationToken);

    public Task<ProjectRecord?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        QuerySingleAsync(
            "SELECT * FROM projects WHERE id = $id;",
            projectId,
            ReadProject,
            cancellationToken);

    public Task<AgentTask?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        QuerySingleAsync(
            "SELECT * FROM tasks WHERE id = $id;",
            taskId,
            ReadTask,
            cancellationToken);

    public Task<CommandRecord?> GetCommandAsync(Guid commandId, CancellationToken cancellationToken = default) =>
        QuerySingleAsync(
            "SELECT * FROM commands WHERE id = $id;",
            commandId,
            ReadCommand,
            cancellationToken);

    public async Task<CommandRegistrationResult> RegisterCommandAsync(
        CommandRecord command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireText(command.IdempotencyKey, nameof(command.IdempotencyKey));
        ValidateJson(command.PayloadJson, nameof(command.PayloadJson));

        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO commands(
                    id, source_device_id, project_id, task_id, idempotency_key,
                    command_type, payload_json, received_at_utc, expires_at_utc,
                    status, processed_at_utc, rejection_reason)
                VALUES(
                    $id, $sourceDeviceId, $projectId, $taskId, $idempotencyKey,
                    $commandType, $payloadJson, $receivedAtUtc, $expiresAtUtc,
                    $status, $processedAtUtc, $rejectionReason);
                """;
            Add(insert, "$id", command.Id);
            Add(insert, "$sourceDeviceId", command.SourceDeviceId);
            AddNullable(insert, "$projectId", command.ProjectId);
            AddNullable(insert, "$taskId", command.TaskId);
            insert.Parameters.AddWithValue("$idempotencyKey", command.IdempotencyKey.Trim());
            insert.Parameters.AddWithValue("$commandType", command.CommandType.ToString());
            insert.Parameters.AddWithValue("$payloadJson", command.PayloadJson);
            Add(insert, "$receivedAtUtc", command.ReceivedAtUtc);
            AddNullable(insert, "$expiresAtUtc", command.ExpiresAtUtc);
            insert.Parameters.AddWithValue("$status", command.Status.ToString());
            AddNullable(insert, "$processedAtUtc", command.ProcessedAtUtc);
            AddNullable(insert, "$rejectionReason", command.RejectionReason);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await InsertAuditAsync(
                connection,
                transaction,
                command.ReceivedAtUtc,
                command.SourceDeviceId,
                "CommandRegistered",
                "Command",
                command.Id.ToString("D"),
                AuditOutcome.Success,
                JsonSerializer.Serialize(new { command.IdempotencyKey, command.CommandType }),
                cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return new CommandRegistrationResult(true, command);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            var existing = await GetCommandByIdempotencyKeyAsync(
                command.SourceDeviceId,
                command.IdempotencyKey.Trim(),
                cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                throw;
            }

            await RecordDuplicateCommandAsync(command, existing, cancellationToken).ConfigureAwait(false);
            return new CommandRegistrationResult(false, existing);
        }
    }

    public async Task CreateTaskAsync(
        AgentTask task,
        Guid sourceCommandId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Status != TaskStatus.Pending || task.Version != 0)
        {
            throw new InvalidOperationException("新任务必须以 Pending 状态和版本 0 创建。");
        }

        RequireText(task.Title, nameof(task.Title));
        RequireText(task.Instruction, nameof(task.Instruction));
        RequireText(task.Executor, nameof(task.Executor));

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var project = await GetProjectInTransactionAsync(
            connection,
            transaction,
            task.ProjectId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("任务关联的项目不存在。");
        if (project.AuthorizationState != ProjectAuthorizationState.Authorized)
        {
            throw new UnauthorizedAccessException("任务关联的项目未获得授权。");
        }

        _ = ProjectPathPolicy.ResolveWithinRoot(project.RootPath, task.WorkingDirectoryRelativePath);
        var sourceCommand = await GetCommandInTransactionAsync(
            connection,
            transaction,
            sourceCommandId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("任务来源命令不存在。");
        ValidateCreateTaskCommand(task, sourceCommand);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO tasks(
                id, project_id, created_by_device_id, title, instruction,
                working_directory_relative_path, executor, status,
                cancellation_requested_at_utc, created_at_utc, updated_at_utc,
                started_at_utc, completed_at_utc, last_heartbeat_at_utc,
                failure_code, failure_message, version)
            VALUES(
                $id, $projectId, $createdByDeviceId, $title, $instruction,
                $workingDirectoryRelativePath, $executor, $status,
                $cancellationRequestedAtUtc, $createdAtUtc, $updatedAtUtc,
                $startedAtUtc, $completedAtUtc, $lastHeartbeatAtUtc,
                $failureCode, $failureMessage, $version);
            """;
        BindTask(insert, task);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var updateCommand = connection.CreateCommand();
        updateCommand.Transaction = transaction;
        updateCommand.CommandText = """
            UPDATE commands
            SET task_id = $taskId,
                status = $status,
                processed_at_utc = $processedAtUtc
            WHERE id = $commandId AND status = $expectedStatus;
            """;
        Add(updateCommand, "$taskId", task.Id);
        updateCommand.Parameters.AddWithValue("$status", CommandStatus.Processed.ToString());
        Add(updateCommand, "$processedAtUtc", task.CreatedAtUtc);
        Add(updateCommand, "$commandId", sourceCommandId);
        updateCommand.Parameters.AddWithValue("$expectedStatus", CommandStatus.Received.ToString());
        if (await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("任务来源命令已经处理，不能重复创建任务。");
        }

        await InsertTaskEventAsync(
            connection,
            transaction,
            new TaskEventRecord
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                SequenceNumber = 1,
                EventType = TaskEventType.Created,
                FromStatus = null,
                ToStatus = TaskStatus.Pending,
                Source = TaskEventSource.User,
                SourceDeviceId = task.CreatedByDeviceId,
                CommandId = sourceCommandId,
                OccurredAtUtc = task.CreatedAtUtc,
                Message = "任务已创建。"
            },
            cancellationToken).ConfigureAwait(false);
        await InsertAuditAsync(
            connection,
            transaction,
            task.CreatedAtUtc,
            task.CreatedByDeviceId,
            "TaskCreated",
            "Task",
            task.Id.ToString("D"),
            AuditOutcome.Success,
            JsonSerializer.Serialize(new { task.ProjectId, sourceCommandId }),
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    public async Task<AgentTask> TransitionTaskAsync(
        Guid taskId,
        TaskStatus newStatus,
        TaskEventSource source,
        string message,
        Guid? sourceDeviceId = null,
        Guid? commandId = null,
        string? dataJson = null,
        string? failureCode = null,
        string? failureMessage = null,
        CancellationToken cancellationToken = default)
    {
        RequireText(message, nameof(message));
        if (dataJson is not null)
        {
            ValidateJson(dataJson, nameof(dataJson));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var current = await GetTaskInTransactionAsync(
            connection,
            transaction,
            taskId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("任务不存在。");
        TaskStateMachine.EnsureTransition(current.Status, newStatus);

        var now = DateTimeOffset.UtcNow;
        var updated = current with
        {
            Status = newStatus,
            UpdatedAtUtc = now,
            StartedAtUtc = newStatus == TaskStatus.Running
                ? current.StartedAtUtc ?? now
                : current.StartedAtUtc,
            CompletedAtUtc = TaskStateMachine.IsTerminal(newStatus) ? now : null,
            FailureCode = newStatus == TaskStatus.Failed ? failureCode : current.FailureCode,
            FailureMessage = newStatus == TaskStatus.Failed ? failureMessage : current.FailureMessage,
            Version = current.Version + 1
        };
        await UpdateTaskAsync(connection, transaction, current.Version, updated, cancellationToken)
            .ConfigureAwait(false);
        var sequence = await GetNextSequenceAsync(connection, transaction, taskId, cancellationToken)
            .ConfigureAwait(false);
        await InsertTaskEventAsync(
            connection,
            transaction,
            new TaskEventRecord
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                SequenceNumber = sequence,
                EventType = TaskEventType.StateChanged,
                FromStatus = current.Status,
                ToStatus = newStatus,
                Source = source,
                SourceDeviceId = sourceDeviceId,
                CommandId = commandId,
                OccurredAtUtc = now,
                Message = message.Trim(),
                DataJson = dataJson
            },
            cancellationToken).ConfigureAwait(false);
        await InsertAuditAsync(
            connection,
            transaction,
            now,
            sourceDeviceId,
            "TaskStateChanged",
            "Task",
            taskId.ToString("D"),
            AuditOutcome.Success,
            JsonSerializer.Serialize(new { from = current.Status, to = newStatus }),
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return updated;
    }

    public async Task<bool> RequestCancellationAsync(
        Guid taskId,
        Guid sourceDeviceId,
        Guid? commandId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var current = await GetTaskInTransactionAsync(
            connection,
            transaction,
            taskId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("任务不存在。");
        var now = DateTimeOffset.UtcNow;
        if (current.Status == TaskStatus.CancellationRequested || TaskStateMachine.IsTerminal(current.Status))
        {
            await InsertAuditAsync(
                connection,
                transaction,
                now,
                sourceDeviceId,
                "TaskCancellationRequested",
                "Task",
                taskId.ToString("D"),
                AuditOutcome.Rejected,
                JsonSerializer.Serialize(new { reason = "task_not_cancellable", current.Status }),
                cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return false;
        }

        TaskStateMachine.EnsureTransition(current.Status, TaskStatus.CancellationRequested);
        var updated = current with
        {
            Status = TaskStatus.CancellationRequested,
            CancellationRequestedAtUtc = now,
            UpdatedAtUtc = now,
            Version = current.Version + 1
        };
        await UpdateTaskAsync(connection, transaction, current.Version, updated, cancellationToken)
            .ConfigureAwait(false);
        var sequence = await GetNextSequenceAsync(connection, transaction, taskId, cancellationToken)
            .ConfigureAwait(false);
        await InsertTaskEventAsync(
            connection,
            transaction,
            new TaskEventRecord
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                SequenceNumber = sequence,
                EventType = TaskEventType.CancellationRequested,
                FromStatus = current.Status,
                ToStatus = TaskStatus.CancellationRequested,
                Source = TaskEventSource.User,
                SourceDeviceId = sourceDeviceId,
                CommandId = commandId,
                OccurredAtUtc = now,
                Message = "用户请求取消任务。"
            },
            cancellationToken).ConfigureAwait(false);
        await InsertAuditAsync(
            connection,
            transaction,
            now,
            sourceDeviceId,
            "TaskCancellationRequested",
            "Task",
            taskId.ToString("D"),
            AuditOutcome.Success,
            null,
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return true;
    }

    public async Task<RecoveryResult> RecoverInterruptedTasksAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var activeTasks = new List<AgentTask>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT * FROM tasks
                WHERE status IN ($running, $waiting, $cancelling)
                ORDER BY created_at_utc;
                """;
            select.Parameters.AddWithValue("$running", TaskStatus.Running.ToString());
            select.Parameters.AddWithValue("$waiting", TaskStatus.WaitingForUser.ToString());
            select.Parameters.AddWithValue("$cancelling", TaskStatus.CancellationRequested.ToString());
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                activeTasks.Add(ReadTask(reader));
            }
        }

        foreach (var current in activeTasks)
        {
            TaskStateMachine.EnsureTransition(current.Status, TaskStatus.Interrupted);
            var updated = current with
            {
                Status = TaskStatus.Interrupted,
                UpdatedAtUtc = recoveredAtUtc,
                Version = current.Version + 1
            };
            await UpdateTaskAsync(connection, transaction, current.Version, updated, cancellationToken)
                .ConfigureAwait(false);
            var sequence = await GetNextSequenceAsync(connection, transaction, current.Id, cancellationToken)
                .ConfigureAwait(false);
            await InsertTaskEventAsync(
                connection,
                transaction,
                new TaskEventRecord
                {
                    Id = Guid.NewGuid(),
                    TaskId = current.Id,
                    SequenceNumber = sequence,
                    EventType = TaskEventType.RecoveryDetected,
                    FromStatus = current.Status,
                    ToStatus = TaskStatus.Interrupted,
                    Source = TaskEventSource.Recovery,
                    OccurredAtUtc = recoveredAtUtc,
                    Message = "程序启动时发现未正常结束的任务，已标记为 Interrupted。"
                },
                cancellationToken).ConfigureAwait(false);
            await InsertAuditAsync(
                connection,
                transaction,
                recoveredAtUtc,
                null,
                "TaskInterruptedDuringRecovery",
                "Task",
                current.Id.ToString("D"),
                AuditOutcome.Success,
                JsonSerializer.Serialize(new { previousStatus = current.Status }),
                cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
        return new RecoveryResult(activeTasks.Select(task => task.Id).ToArray());
    }

    public async Task<IReadOnlyList<TaskEventRecord>> GetTaskEventsAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM task_events WHERE task_id = $taskId ORDER BY sequence_number;";
        Add(command, "$taskId", taskId);
        var events = new List<TaskEventRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(ReadTaskEvent(reader));
        }

        return events;
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetAuditLogAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM audit_log ORDER BY occurred_at_utc, id;";
        var entries = new List<AuditLogEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(ReadAudit(reader));
        }

        return entries;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task<T?> QuerySingleAsync<T>(
        string sql,
        Guid id,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken)
        where T : class
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? map(reader) : null;
    }

    private async Task<CommandRecord?> GetCommandByIdempotencyKeyAsync(
        Guid sourceDeviceId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM commands
            WHERE source_device_id = $sourceDeviceId AND idempotency_key = $idempotencyKey;
            """;
        Add(command, "$sourceDeviceId", sourceDeviceId);
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadCommand(reader) : null;
    }

    private async Task RecordDuplicateCommandAsync(
        CommandRecord duplicate,
        CommandRecord existing,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await InsertAuditAsync(
            connection,
            transaction,
            DateTimeOffset.UtcNow,
            duplicate.SourceDeviceId,
            "CommandDuplicateIgnored",
            "Command",
            existing.Id.ToString("D"),
            AuditOutcome.Rejected,
            JsonSerializer.Serialize(new
            {
                duplicateCommandId = duplicate.Id,
                duplicate.IdempotencyKey
            }),
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    private static async Task<ProjectRecord?> GetProjectInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM projects WHERE id = $id;";
        Add(command, "$id", projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadProject(reader) : null;
    }

    private static async Task<AgentTask?> GetTaskInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM tasks WHERE id = $id;";
        Add(command, "$id", taskId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadTask(reader) : null;
    }

    private static async Task<CommandRecord?> GetCommandInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM commands WHERE id = $id;";
        Add(command, "$id", commandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadCommand(reader) : null;
    }

    private static void ValidateCreateTaskCommand(AgentTask task, CommandRecord sourceCommand)
    {
        if (sourceCommand.CommandType != CommandType.CreateTask || sourceCommand.Status != CommandStatus.Received)
        {
            throw new InvalidOperationException("来源命令不是尚未处理的 CreateTask 命令。");
        }

        if (sourceCommand.SourceDeviceId != task.CreatedByDeviceId || sourceCommand.ProjectId != task.ProjectId)
        {
            throw new InvalidOperationException("来源命令与任务的设备或项目不一致。");
        }

        if (sourceCommand.ExpiresAtUtc is { } expiresAt && expiresAt <= task.CreatedAtUtc)
        {
            throw new InvalidOperationException("来源命令已经过期。");
        }
    }

    private static async Task UpdateTaskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long expectedVersion,
        AgentTask task,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE tasks SET
                status = $status,
                cancellation_requested_at_utc = $cancellationRequestedAtUtc,
                updated_at_utc = $updatedAtUtc,
                started_at_utc = $startedAtUtc,
                completed_at_utc = $completedAtUtc,
                last_heartbeat_at_utc = $lastHeartbeatAtUtc,
                failure_code = $failureCode,
                failure_message = $failureMessage,
                version = $version
            WHERE id = $id AND version = $expectedVersion;
            """;
        Add(command, "$id", task.Id);
        command.Parameters.AddWithValue("$status", task.Status.ToString());
        AddNullable(command, "$cancellationRequestedAtUtc", task.CancellationRequestedAtUtc);
        Add(command, "$updatedAtUtc", task.UpdatedAtUtc);
        AddNullable(command, "$startedAtUtc", task.StartedAtUtc);
        AddNullable(command, "$completedAtUtc", task.CompletedAtUtc);
        AddNullable(command, "$lastHeartbeatAtUtc", task.LastHeartbeatAtUtc);
        AddNullable(command, "$failureCode", task.FailureCode);
        AddNullable(command, "$failureMessage", task.FailureMessage);
        command.Parameters.AddWithValue("$version", task.Version);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("任务版本已变化，请重新读取后再操作。");
        }
    }

    private static async Task<long> GetNextSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(sequence_number), 0) + 1 FROM task_events WHERE task_id = $taskId;";
        Add(command, "$taskId", taskId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task InsertTaskEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TaskEventRecord taskEvent,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO task_events(
                id, task_id, sequence_number, event_type, from_status, to_status,
                source, source_device_id, command_id, occurred_at_utc, message, data_json)
            VALUES(
                $id, $taskId, $sequenceNumber, $eventType, $fromStatus, $toStatus,
                $source, $sourceDeviceId, $commandId, $occurredAtUtc, $message, $dataJson);
            """;
        Add(command, "$id", taskEvent.Id);
        Add(command, "$taskId", taskEvent.TaskId);
        command.Parameters.AddWithValue("$sequenceNumber", taskEvent.SequenceNumber);
        command.Parameters.AddWithValue("$eventType", taskEvent.EventType.ToString());
        AddNullable(command, "$fromStatus", taskEvent.FromStatus?.ToString());
        AddNullable(command, "$toStatus", taskEvent.ToStatus?.ToString());
        command.Parameters.AddWithValue("$source", taskEvent.Source.ToString());
        AddNullable(command, "$sourceDeviceId", taskEvent.SourceDeviceId);
        AddNullable(command, "$commandId", taskEvent.CommandId);
        Add(command, "$occurredAtUtc", taskEvent.OccurredAtUtc);
        command.Parameters.AddWithValue("$message", taskEvent.Message);
        AddNullable(command, "$dataJson", taskEvent.DataJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset occurredAtUtc,
        Guid? actorDeviceId,
        string action,
        string entityType,
        string entityId,
        AuditOutcome outcome,
        string? detailsJson,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit_log(
                id, occurred_at_utc, actor_device_id, action,
                entity_type, entity_id, outcome, details_json)
            VALUES(
                $id, $occurredAtUtc, $actorDeviceId, $action,
                $entityType, $entityId, $outcome, $detailsJson);
            """;
        Add(command, "$id", Guid.NewGuid());
        Add(command, "$occurredAtUtc", occurredAtUtc);
        AddNullable(command, "$actorDeviceId", actorDeviceId);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$entityId", entityId);
        command.Parameters.AddWithValue("$outcome", outcome.ToString());
        AddNullable(command, "$detailsJson", detailsJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindTask(SqliteCommand command, AgentTask task)
    {
        Add(command, "$id", task.Id);
        Add(command, "$projectId", task.ProjectId);
        Add(command, "$createdByDeviceId", task.CreatedByDeviceId);
        command.Parameters.AddWithValue("$title", task.Title.Trim());
        command.Parameters.AddWithValue("$instruction", task.Instruction.Trim());
        command.Parameters.AddWithValue("$workingDirectoryRelativePath", task.WorkingDirectoryRelativePath);
        command.Parameters.AddWithValue("$executor", task.Executor.Trim());
        command.Parameters.AddWithValue("$status", task.Status.ToString());
        AddNullable(command, "$cancellationRequestedAtUtc", task.CancellationRequestedAtUtc);
        Add(command, "$createdAtUtc", task.CreatedAtUtc);
        Add(command, "$updatedAtUtc", task.UpdatedAtUtc);
        AddNullable(command, "$startedAtUtc", task.StartedAtUtc);
        AddNullable(command, "$completedAtUtc", task.CompletedAtUtc);
        AddNullable(command, "$lastHeartbeatAtUtc", task.LastHeartbeatAtUtc);
        AddNullable(command, "$failureCode", task.FailureCode);
        AddNullable(command, "$failureMessage", task.FailureMessage);
        command.Parameters.AddWithValue("$version", task.Version);
    }

    private static DeviceRecord ReadDevice(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        DisplayName = reader.GetString(reader.GetOrdinal("display_name")),
        DeviceType = ReadEnum<DeviceType>(reader, "device_type"),
        TrustState = ReadEnum<DeviceTrustState>(reader, "trust_state"),
        PublicKeyThumbprint = ReadNullableString(reader, "public_key_thumbprint"),
        CreatedAtUtc = ReadDateTime(reader, "created_at_utc"),
        LastSeenAtUtc = ReadNullableDateTime(reader, "last_seen_at_utc"),
        RevokedAtUtc = ReadNullableDateTime(reader, "revoked_at_utc")
    };

    private static ProjectRecord ReadProject(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        Name = reader.GetString(reader.GetOrdinal("name")),
        RootPath = reader.GetString(reader.GetOrdinal("root_path")),
        AuthorizationState = ReadEnum<ProjectAuthorizationState>(reader, "authorization_state"),
        AuthorizedByDeviceId = ReadGuid(reader, "authorized_by_device_id"),
        AuthorizedAtUtc = ReadDateTime(reader, "authorized_at_utc"),
        RevokedAtUtc = ReadNullableDateTime(reader, "revoked_at_utc"),
        CreatedAtUtc = ReadDateTime(reader, "created_at_utc"),
        UpdatedAtUtc = ReadDateTime(reader, "updated_at_utc")
    };

    private static AgentTask ReadTask(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        ProjectId = ReadGuid(reader, "project_id"),
        CreatedByDeviceId = ReadGuid(reader, "created_by_device_id"),
        Title = reader.GetString(reader.GetOrdinal("title")),
        Instruction = reader.GetString(reader.GetOrdinal("instruction")),
        WorkingDirectoryRelativePath = reader.GetString(reader.GetOrdinal("working_directory_relative_path")),
        Executor = reader.GetString(reader.GetOrdinal("executor")),
        Status = ReadEnum<TaskStatus>(reader, "status"),
        CancellationRequestedAtUtc = ReadNullableDateTime(reader, "cancellation_requested_at_utc"),
        CreatedAtUtc = ReadDateTime(reader, "created_at_utc"),
        UpdatedAtUtc = ReadDateTime(reader, "updated_at_utc"),
        StartedAtUtc = ReadNullableDateTime(reader, "started_at_utc"),
        CompletedAtUtc = ReadNullableDateTime(reader, "completed_at_utc"),
        LastHeartbeatAtUtc = ReadNullableDateTime(reader, "last_heartbeat_at_utc"),
        FailureCode = ReadNullableString(reader, "failure_code"),
        FailureMessage = ReadNullableString(reader, "failure_message"),
        Version = reader.GetInt64(reader.GetOrdinal("version"))
    };

    private static CommandRecord ReadCommand(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        SourceDeviceId = ReadGuid(reader, "source_device_id"),
        ProjectId = ReadNullableGuid(reader, "project_id"),
        TaskId = ReadNullableGuid(reader, "task_id"),
        IdempotencyKey = reader.GetString(reader.GetOrdinal("idempotency_key")),
        CommandType = ReadEnum<CommandType>(reader, "command_type"),
        PayloadJson = reader.GetString(reader.GetOrdinal("payload_json")),
        ReceivedAtUtc = ReadDateTime(reader, "received_at_utc"),
        ExpiresAtUtc = ReadNullableDateTime(reader, "expires_at_utc"),
        Status = ReadEnum<CommandStatus>(reader, "status"),
        ProcessedAtUtc = ReadNullableDateTime(reader, "processed_at_utc"),
        RejectionReason = ReadNullableString(reader, "rejection_reason")
    };

    private static TaskEventRecord ReadTaskEvent(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        TaskId = ReadGuid(reader, "task_id"),
        SequenceNumber = reader.GetInt64(reader.GetOrdinal("sequence_number")),
        EventType = ReadEnum<TaskEventType>(reader, "event_type"),
        FromStatus = ReadNullableEnum<TaskStatus>(reader, "from_status"),
        ToStatus = ReadNullableEnum<TaskStatus>(reader, "to_status"),
        Source = ReadEnum<TaskEventSource>(reader, "source"),
        SourceDeviceId = ReadNullableGuid(reader, "source_device_id"),
        CommandId = ReadNullableGuid(reader, "command_id"),
        OccurredAtUtc = ReadDateTime(reader, "occurred_at_utc"),
        Message = reader.GetString(reader.GetOrdinal("message")),
        DataJson = ReadNullableString(reader, "data_json")
    };

    private static AuditLogEntry ReadAudit(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        OccurredAtUtc = ReadDateTime(reader, "occurred_at_utc"),
        ActorDeviceId = ReadNullableGuid(reader, "actor_device_id"),
        Action = reader.GetString(reader.GetOrdinal("action")),
        EntityType = reader.GetString(reader.GetOrdinal("entity_type")),
        EntityId = reader.GetString(reader.GetOrdinal("entity_id")),
        Outcome = ReadEnum<AuditOutcome>(reader, "outcome"),
        DetailsJson = ReadNullableString(reader, "details_json")
    };

    private static void Add(SqliteCommand command, string name, Guid value) =>
        command.Parameters.AddWithValue(name, value.ToString("D"));

    private static void Add(SqliteCommand command, string name, DateTimeOffset value) =>
        command.Parameters.AddWithValue(name, ToDb(value));

    private static void AddNullable(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(
            name,
            value switch
            {
                null => DBNull.Value,
                Guid guid => guid.ToString("D"),
                DateTimeOffset dateTime => ToDb(dateTime),
                _ => value
            });
    }

    private static string ToDb(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static Guid ReadGuid(SqliteDataReader reader, string name) =>
        Guid.Parse(reader.GetString(reader.GetOrdinal(name)));

    private static Guid? ReadNullableGuid(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));
    }

    private static DateTimeOffset ReadDateTime(SqliteDataReader reader, string name) =>
        DateTimeOffset.Parse(
            reader.GetString(reader.GetOrdinal(name)),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ReadNullableDateTime(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(
                reader.GetString(ordinal),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
    }

    private static string? ReadNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static T ReadEnum<T>(SqliteDataReader reader, string name)
        where T : struct, Enum =>
        Enum.Parse<T>(reader.GetString(reader.GetOrdinal(name)), ignoreCase: false);

    private static T? ReadNullableEnum<T>(SqliteDataReader reader, string name)
        where T : struct, Enum
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal)
            ? null
            : Enum.Parse<T>(reader.GetString(ordinal), ignoreCase: false);
    }

    private static void RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("值不能为空。", parameterName);
        }
    }

    private static void ValidateJson(string json, string parameterName)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("值必须是有效 JSON。", parameterName, exception);
        }
    }
}
