using System.Diagnostics;
using System.Text.Json;
using ScreenGuide.Core.Tasking;
using ScreenGuide.Core.Sessions;
using Microsoft.Data.Sqlite;
using ScreenGuide.Persistence.Runtime;
using ScreenGuide.Persistence.Sqlite;
using AgentTaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

namespace ScreenGuide.Tasking.Tests;

public sealed class SqliteTaskStoreTests
{
    [Fact]
    public async Task ExactAnnotatedStage1SourceBuildsDedicatedCompatibilityRunner()
    {
        var evidence = await RunStage1CompatibilityScenarioAsync("Identity");

        Assert.True(evidence.GetProperty("success").GetBoolean());
        Assert.True(evidence.GetProperty("tagObjectVerified").GetBoolean());
        Assert.True(evidence.GetProperty("sourceCommitVerified").GetBoolean());
        Assert.True(evidence.GetProperty("stage1RunnerBuilt").GetBoolean());
        Assert.Equal(7, evidence.GetProperty("stage1SchemaVersion").GetInt32());
        Assert.Equal(8, evidence.GetProperty("currentSchemaVersion").GetInt32());
    }

    [Fact]
    public async Task ExactStage1ReadsThePreV8BackupAfterTheCurrentStoreMigratesItsV7Database()
    {
        var evidence = await RunStage1CompatibilityScenarioAsync("NormalMigration");

        Assert.True(evidence.GetProperty("success").GetBoolean());
        Assert.Equal(8, evidence.GetProperty("mainSchemaVersion").GetInt32());
        Assert.Equal(7, evidence.GetProperty("backupSchemaVersion").GetInt32());
        Assert.True(evidence.GetProperty("uniqueBackup").GetBoolean());
        Assert.True(evidence.GetProperty("mainIntegrityOk").GetBoolean());
        Assert.True(evidence.GetProperty("backupIntegrityOk").GetBoolean());
        Assert.True(evidence.GetProperty("mainCanaryReadable").GetBoolean());
        Assert.True(evidence.GetProperty("backupCanaryReadableByExactStage1").GetBoolean());
        Assert.True(evidence.GetProperty("historicalFrozenRouteNull").GetBoolean());
        Assert.True(evidence.GetProperty("v8ObjectsOnlyInMain").GetBoolean());
    }

    [Fact]
    public async Task FailedVersion8MigrationRollsBackAtomicallyAndKeepsItsPreV8BackupReadable()
    {
        var evidence = await RunStage1CompatibilityScenarioAsync("FailureRollback");

        Assert.True(evidence.GetProperty("success").GetBoolean());
        Assert.True(evidence.GetProperty("migrationFailed").GetBoolean());
        Assert.Equal(7, evidence.GetProperty("mainSchemaVersion").GetInt32());
        Assert.Equal(7, evidence.GetProperty("backupSchemaVersion").GetInt32());
        Assert.True(evidence.GetProperty("schemaFingerprintUnchanged").GetBoolean());
        Assert.True(evidence.GetProperty("noPartialFrozenRouteColumns").GetBoolean());
        Assert.True(evidence.GetProperty("noPartialVersion8Indexes").GetBoolean());
        Assert.True(evidence.GetProperty("preexistingConflictPreserved").GetBoolean());
        Assert.True(evidence.GetProperty("mainIntegrityOk").GetBoolean());
        Assert.True(evidence.GetProperty("backupIntegrityOk").GetBoolean());
        Assert.True(evidence.GetProperty("mainCanaryReadableByExactStage1").GetBoolean());
        Assert.True(evidence.GetProperty("backupCanaryReadableByExactStage1").GetBoolean());
    }

    [Fact]
    public async Task ExactStage1RejectsAnIsolatedV8CopyWithoutChangingItsBytesOrLogicalState()
    {
        var evidence = await RunStage1CompatibilityScenarioAsync("RejectV8");

        Assert.True(evidence.GetProperty("success").GetBoolean());
        Assert.True(evidence.GetProperty("stage1RejectedV8").GetBoolean());
        Assert.Equal(8, evidence.GetProperty("schemaVersion").GetInt32());
        Assert.True(evidence.GetProperty("databaseHashUnchanged").GetBoolean());
        Assert.True(evidence.GetProperty("schemaFingerprintUnchanged").GetBoolean());
        Assert.True(evidence.GetProperty("canaryUnchanged").GetBoolean());
        Assert.True(evidence.GetProperty("hostNotStarted").GetBoolean());
        Assert.True(evidence.GetProperty("noTurnReplay").GetBoolean());
        Assert.True(evidence.GetProperty("noInPlaceDowngrade").GetBoolean());
    }

    [Fact]
    public async Task FreshDatabaseUsesV02SchemaAndCreatesExplicitResourceScope()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var (task, _) = await environment.CreateTaskAsync("v02-fresh-schema");

        var schemaVersion = await environment.Store.GetSchemaVersionAsync();
        var authorization = await environment.Store.GetProjectAuthorizationAsync(environment.Project.Id);
        var scope = Assert.Single(await environment.Store.GetResourceScopesAsync(task.Id));

        Assert.Equal(V02Contract.SchemaVersion, schemaVersion);
        Assert.Equal(environment.Project.Id, authorization?.ProjectId);
        Assert.Equal(ProjectAuthorizationScope.ProjectDirectory, authorization?.Scope);
        Assert.Equal(ResourceScopeType.Project, scope.ScopeType);
        Assert.Equal(ResourceAccessMode.Execute, scope.AccessMode);
        Assert.Equal(environment.Project.Id, scope.ResourceId);
        Assert.Equal(environment.Project.RootPath, scope.ScopeValue);
    }

    [Fact]
    public async Task MigratesVersion6SessionTurnsToLatestSchemaWithoutLosingExistingData()
    {
        var root = Path.Combine(Path.GetTempPath(), $"screen-guide-v6-migration-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(root, "state", "tasking.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var deviceId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 24, 2, 3, 4, TimeSpan.Zero);

        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = SqliteSchema.CreateVersion1
                    + SqliteSchema.CreateVersion2
                    + SqliteSchema.CreateVersion3
                    + SqliteSchema.CreateVersion4
                    + SqliteSchema.CreateVersion5
                    + SqliteSchema.CreateVersion6
                    + """
                      INSERT INTO schema_info(version, applied_at_utc)
                      VALUES(1, $now), (2, $now), (3, $now), (4, $now), (5, $now), (6, $now);
                      INSERT INTO devices(id, display_name, device_type, trust_state, created_at_utc)
                      VALUES($deviceId, 'Migration Host', 'WindowsHost', 'Local', $now);
                      INSERT INTO conversations(
                          id, created_by_device_id, title, provider_id, status,
                          created_at_utc, updated_at_utc, version)
                      VALUES($conversationId, $deviceId, 'Existing session', 'codex', 'Active', $now, $now, 0);
                      INSERT INTO sessions(
                          id, conversation_id, created_by_device_id, title, status, is_current,
                          created_at_utc, updated_at_utc, last_active_at_utc, version)
                      VALUES($conversationId, $conversationId, $deviceId, 'Existing session', 'Active', 1,
                          $now, $now, $now, 0);
                      INSERT INTO session_turns(
                          id, session_id, sequence_number, input_text, input_modality, idempotency_key,
                          work_kind, phase, missing_context, intent_kind,
                          requires_confirmation, confirmation_granted, cancellation_requested,
                          created_at_utc, updated_at_utc, version)
                      VALUES($turnId, $conversationId, 1, '打开记事本', 'Text', 'existing-v6-turn',
                          'DesktopAction', 'WaitingForConfirmation', 'Confirmation', 'OpenApplication',
                          1, 0, 0, $now, $now, 3);
                      """;
                command.Parameters.AddWithValue("$now", now.ToString("O"));
                command.Parameters.AddWithValue("$deviceId", deviceId.ToString("D"));
                command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
                command.Parameters.AddWithValue("$turnId", turnId.ToString("D"));
                await command.ExecuteNonQueryAsync();
            }

            await using (var store = new SqliteTaskStore(databasePath))
            {
                await store.InitializeAsync();
                Assert.Equal(V02Contract.SchemaVersion, await store.GetSchemaVersionAsync());
            }

            await using (var sessions = new SqliteSessionStore(databasePath))
            {
                await sessions.InitializeAsync();
                var stored = await sessions.GetTurnAsync(turnId);

                Assert.Equal("打开记事本", stored?.InputText);
                Assert.Equal("OpenApplication", stored?.IntentKind);
                Assert.Equal(3, stored?.Version);
                Assert.Null(stored?.ExpectedIntentKind);
                Assert.Null(stored?.ExpectedTarget);
                Assert.Null(stored?.PlanTarget);
            }

            var invocations = new SqliteAiInvocationStore(databasePath);
            await invocations.InitializeAsync();

            Assert.Single(Directory.GetFiles(
                Path.GetDirectoryName(databasePath)!,
                $"tasking.pre-v{V02Contract.SchemaVersion}-from-v6-*.backup.db"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MigratesStage1Version7DatabaseToAiInvocationSchemaWithoutLosingConversationHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"screen-guide-v7-migration-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(root, "state", "tasking.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var deviceId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var sessionTurnId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 24, 3, 4, 5, TimeSpan.Zero);

        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = SqliteSchema.CreateVersion1
                    + SqliteSchema.CreateVersion2
                    + SqliteSchema.CreateVersion3
                    + SqliteSchema.CreateVersion4
                    + SqliteSchema.CreateVersion5
                    + SqliteSchema.CreateVersion6
                    + SqliteSchema.CreateVersion7
                    + """
                      INSERT INTO schema_info(version, applied_at_utc)
                      VALUES(1, $now), (2, $now), (3, $now), (4, $now),
                            (5, $now), (6, $now), (7, $now);
                      INSERT INTO devices(id, display_name, device_type, trust_state, created_at_utc)
                      VALUES($deviceId, 'Stage 1 Host', 'WindowsHost', 'Local', $now);
                      INSERT INTO conversations(
                          id, created_by_device_id, title, provider_id, status,
                          created_at_utc, updated_at_utc, last_message_at_utc, version)
                      VALUES($conversationId, $deviceId, 'Stage 1 conversation', 'codex-conversation',
                          'Ready', $now, $now, $now, 2);
                      INSERT INTO conversation_messages(
                          id, conversation_id, sequence_number, role, content, created_at_utc)
                      VALUES($messageId, $conversationId, 1, 'User', '阶段一历史不能丢', $now);
                      INSERT INTO sessions(
                          id, conversation_id, created_by_device_id, title, status, is_current,
                          created_at_utc, updated_at_utc, last_active_at_utc, version)
                      VALUES($sessionId, $conversationId, $deviceId, 'Stage 1 session', 'Active', 1,
                          $now, $now, $now, 0);
                      INSERT INTO session_turns(
                          id, session_id, sequence_number, input_text, input_modality,
                          idempotency_key, work_kind, phase, missing_context,
                          created_at_utc, updated_at_utc, version)
                      VALUES($sessionTurnId, $sessionId, 1, '阶段一历史 Turn', 'Text',
                          'stage1-historical-turn', 'Conversation', 'Completed', 'None',
                          $now, $now, 0);
                      """;
                command.Parameters.AddWithValue("$now", now.ToString("O"));
                command.Parameters.AddWithValue("$deviceId", deviceId.ToString("D"));
                command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
                command.Parameters.AddWithValue("$messageId", messageId.ToString("D"));
                command.Parameters.AddWithValue("$sessionId", sessionId.ToString("D"));
                command.Parameters.AddWithValue("$sessionTurnId", sessionTurnId.ToString("D"));
                await command.ExecuteNonQueryAsync();
            }

            await using (var store = new SqliteTaskStore(databasePath))
            {
                await store.InitializeAsync();
                Assert.Equal(V02Contract.SchemaVersion, await store.GetSchemaVersionAsync());
            }

            {
                var conversations = new SqliteConversationStore(databasePath);
                await conversations.InitializeAsync();
                var history = await conversations.GetMessagesAsync(conversationId);
                Assert.Equal("阶段一历史不能丢", Assert.Single(history).Content);
            }

            await using (var sessions = new SqliteSessionStore(databasePath))
            {
                await sessions.InitializeAsync();
                var historicalTurn = await sessions.GetTurnAsync(sessionTurnId);
                Assert.Equal("阶段一历史 Turn", historicalTurn?.InputText);
                Assert.Null(historicalTurn?.FrozenRoute);
            }

            await new SqliteAiInvocationStore(databasePath).InitializeAsync();
            Assert.Single(Directory.GetFiles(
                Path.GetDirectoryName(databasePath)!,
                $"tasking.pre-v{V02Contract.SchemaVersion}-from-v7-*.backup.db"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task IncompleteCandidateVersion8FailsClosedWithoutAddingFrozenRouteColumns()
    {
        var root = Path.Combine(Path.GetTempPath(), $"screen-guide-incomplete-v8-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(root, "state", "tasking.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var now = new DateTimeOffset(2026, 8, 25, 1, 0, 0, TimeSpan.Zero);

        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = SqliteSchema.CreateVersion1
                    + SqliteSchema.CreateVersion2
                    + SqliteSchema.CreateVersion3
                    + SqliteSchema.CreateVersion4
                    + SqliteSchema.CreateVersion5
                    + SqliteSchema.CreateVersion6
                    + SqliteSchema.CreateVersion7
                    + """
                      CREATE TABLE ai_invocations (
                          id TEXT NOT NULL PRIMARY KEY,
                          session_turn_id TEXT NULL,
                          conversation_turn_id TEXT NULL,
                          purpose TEXT NOT NULL,
                          provider_id TEXT NOT NULL,
                          model_id TEXT NOT NULL,
                          prompt_id TEXT NOT NULL,
                          prompt_version TEXT NOT NULL,
                          prompt_content_hash TEXT NOT NULL,
                          data_destination TEXT NOT NULL,
                          status TEXT NOT NULL,
                          started_at_utc TEXT NOT NULL,
                          completed_at_utc TEXT NULL,
                          finish_reason TEXT NULL,
                          input_tokens INTEGER NULL,
                          output_tokens INTEGER NULL,
                          total_tokens INTEGER NULL,
                          provider_request_id TEXT NULL,
                          failure_code TEXT NULL,
                          FOREIGN KEY (session_turn_id) REFERENCES session_turns(id) ON DELETE SET NULL,
                          FOREIGN KEY (conversation_turn_id) REFERENCES conversation_turns(id) ON DELETE SET NULL
                      );
                      CREATE INDEX ix_ai_invocations_conversation_turn
                          ON ai_invocations(conversation_turn_id, started_at_utc);
                      CREATE INDEX ix_ai_invocations_session_turn
                          ON ai_invocations(session_turn_id, started_at_utc);
                      CREATE INDEX ix_ai_invocations_provider_model
                          ON ai_invocations(provider_id, model_id, started_at_utc);
                      INSERT INTO schema_info(version, applied_at_utc)
                      VALUES(1, $now), (2, $now), (3, $now), (4, $now),
                            (5, $now), (6, $now), (7, $now), (8, $now);
                      """;
                command.Parameters.AddWithValue("$now", now.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            await using (var taskStore = new SqliteTaskStore(databasePath))
            {
                await taskStore.InitializeAsync();
                Assert.Equal(8, await taskStore.GetSchemaVersionAsync());
            }

            var before = await ReadSessionTurnColumnsAsync(databasePath);
            await using var sessions = new SqliteSessionStore(databasePath);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sessions.InitializeAsync());
            var after = await ReadSessionTurnColumnsAsync(databasePath);

            Assert.Contains("schema v8 不完整", exception.Message, StringComparison.Ordinal);
            Assert.Contains("frozen_route_status", exception.Message, StringComparison.Ordinal);
            Assert.Equal(before, after);
            Assert.DoesNotContain("frozen_route_status", after);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MigratesVersion3WithoutLosingTaskAndCreatesBackup()
    {
        var root = Path.Combine(Path.GetTempPath(), $"screen-guide-v3-migration-{Guid.NewGuid():N}");
        var projectRoot = Path.Combine(root, "project");
        var databasePath = Path.Combine(root, "state", "tasking.db");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var deviceId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 18, 1, 2, 3, TimeSpan.Zero);

        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = SqliteSchema.CreateVersion1
                    + SqliteSchema.CreateVersion2
                    + SqliteSchema.CreateVersion3
                    + """
                      INSERT INTO schema_info(version, applied_at_utc) VALUES(1, $now), (2, $now), (3, $now);
                      INSERT INTO devices(id, display_name, device_type, trust_state, created_at_utc)
                      VALUES($deviceId, 'Migration Host', 'WindowsHost', 'Local', $now);
                      INSERT INTO projects(
                          id, name, root_path, authorization_state, authorized_by_device_id,
                          authorized_at_utc, created_at_utc, updated_at_utc)
                      VALUES($projectId, 'Existing Project', $rootPath, 'Authorized', $deviceId, $now, $now, $now);
                      INSERT INTO tasks(
                          id, project_id, created_by_device_id, title, instruction,
                          working_directory_relative_path, executor, status,
                          created_at_utc, updated_at_utc, started_at_utc, version)
                      VALUES(
                          $taskId, $projectId, $deviceId, 'Existing task', 'keep me',
                          '.', 'codex', 'Running', $now, $now, $now, 7);
                      """;
                command.Parameters.AddWithValue("$now", now.ToString("O"));
                command.Parameters.AddWithValue("$deviceId", deviceId.ToString("D"));
                command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
                command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
                command.Parameters.AddWithValue("$rootPath", projectRoot);
                await command.ExecuteNonQueryAsync();
            }

            await using (var store = new SqliteTaskStore(databasePath))
            {
                await store.InitializeAsync();
                var task = await store.GetTaskAsync(taskId);
                var authorization = await store.GetProjectAuthorizationAsync(projectId);
                var scope = Assert.Single(await store.GetResourceScopesAsync(taskId));

                Assert.Equal(V02Contract.SchemaVersion, await store.GetSchemaVersionAsync());
                Assert.Equal(AgentTaskStatus.Running, task?.Status);
                Assert.Equal(TaskPhase.Executing, task?.Phase);
                Assert.Equal(7, task?.Version);
                Assert.Equal("keep me", task?.Instruction);
                Assert.Equal(projectRoot, authorization?.ScopeValue);
                Assert.Equal(projectId, scope.ResourceId);
            }

            Assert.Single(Directory.GetFiles(
                Path.GetDirectoryName(databasePath)!,
                $"tasking.pre-v{V02Contract.SchemaVersion}-from-v3-*.backup.db"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PersistsPhaseScopeAndSkillInvocationWithAudit()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var (task, _) = await environment.CreateTaskAsync("v02-domain-records");

        var routing = await environment.Store.TransitionTaskPhaseAsync(
            task.Id,
            TaskPhase.Routing,
            TaskEventSource.System,
            "正在选择执行技能。");
        var scope = new ResourceScopeRecord
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            ScopeType = ResourceScopeType.Directory,
            ScopeValue = "src",
            AccessMode = ResourceAccessMode.Read,
            GrantedByDeviceId = environment.Device.Id,
            GrantedAtUtc = DateTimeOffset.UtcNow
        };
        var invocation = new SkillInvocationRecord
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            SequenceNumber = 1,
            SkillId = "codex.project-task",
            SkillVersion = "0.2.0",
            Capability = "coding.execute",
            InputJson = "{\"instruction\":\"safe test\"}",
            Status = SkillInvocationStatus.Running,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        await environment.Store.UpsertResourceScopeAsync(scope);
        await environment.Store.UpsertSkillInvocationAsync(invocation);
        var events = await environment.Store.GetTaskEventsAsync(task.Id);
        var audit = await environment.Store.GetAuditLogAsync();

        Assert.Equal(TaskPhase.Routing, routing.Phase);
        Assert.Contains(events, item =>
            item.EventType == TaskEventType.PhaseChanged
            && item.FromPhase == TaskPhase.Planning
            && item.ToPhase == TaskPhase.Routing);
        Assert.Contains(await environment.Store.GetResourceScopesAsync(task.Id), item => item.Id == scope.Id);
        Assert.Equal(SkillInvocationStatus.Running,
            Assert.Single(await environment.Store.GetSkillInvocationsAsync(task.Id)).Status);
        Assert.Contains(audit, item => item.Action == "TaskPhaseChanged");
        Assert.Contains(audit, item => item.Action == "ResourceScopeRecorded");
        Assert.Contains(audit, item => item.Action == "SkillInvocationRecorded");
    }

    [Fact]
    public async Task PersistsTaskCommandEventAndAuditAcrossStoreInstances()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var (task, command) = await environment.CreateTaskAsync();

        await using var reopened = new SqliteTaskStore(environment.DatabasePath);
        await reopened.InitializeAsync();
        var persistedTask = await reopened.GetTaskAsync(task.Id);
        var persistedCommand = await reopened.GetCommandAsync(command.Id);
        var events = await reopened.GetTaskEventsAsync(task.Id);
        var audit = await reopened.GetAuditLogAsync();

        Assert.NotNull(persistedTask);
        Assert.Equal(AgentTaskStatus.Pending, persistedTask.Status);
        Assert.NotNull(persistedCommand);
        Assert.Equal(CommandStatus.Processed, persistedCommand.Status);
        Assert.Equal(task.Id, persistedCommand.TaskId);
        Assert.Single(events);
        Assert.Equal(TaskEventType.Created, events[0].EventType);
        Assert.Contains(audit, entry => entry.Action == "TaskCreated" && entry.EntityId == task.Id.ToString("D"));
    }

    [Fact]
    public async Task AcceptsConcurrentDuplicateCommandOnlyOnce()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var registrations = await Task.WhenAll(
            Enumerable.Range(0, 12)
                .Select(_ => environment.Store.RegisterCommandAsync(
                    environment.NewCreateCommand("same-device-command"))));

        Assert.Single(registrations, result => result.Accepted);
        var persistedIds = registrations.Select(result => result.Command.Id).Distinct().ToArray();
        Assert.Single(persistedIds);
        var audit = await environment.Store.GetAuditLogAsync();
        Assert.Contains(audit, entry => entry.Action == "CommandDuplicateIgnored");
    }

    [Fact]
    public async Task ListsOnlyCommandsBoundToRequestedTask()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var (task, sourceCommand) = await environment.CreateTaskAsync("task-command-list");
        _ = await environment.Store.RegisterCommandAsync(environment.NewCreateCommand("unbound-command"));

        var commands = await environment.Store.GetTaskCommandsAsync(task.Id);

        Assert.Equal(sourceCommand.Id, Assert.Single(commands).Id);
    }

    [Fact]
    public async Task IdempotencyKeyIsScopedToSourceDevice()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var secondDevice = environment.Device with
        {
            Id = Guid.NewGuid(),
            DisplayName = "Second test device"
        };
        await environment.Store.UpsertDeviceAsync(secondDevice);
        var first = environment.NewCreateCommand("shared-key");
        var second = environment.NewCreateCommand("shared-key") with
        {
            Id = Guid.NewGuid(),
            SourceDeviceId = secondDevice.Id
        };

        var firstResult = await environment.Store.RegisterCommandAsync(first);
        var secondResult = await environment.Store.RegisterCommandAsync(second);

        Assert.True(firstResult.Accepted);
        Assert.True(secondResult.Accepted);
        Assert.NotEqual(firstResult.Command.Id, secondResult.Command.Id);
    }

    [Fact]
    public async Task RejectsTaskWhenProjectAuthorizationWasRevoked()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var revokedAt = DateTimeOffset.UtcNow;
        await environment.Store.SetProjectAuthorizationAsync(environment.Project with
        {
            AuthorizationState = ProjectAuthorizationState.Revoked,
            RevokedAtUtc = revokedAt,
            UpdatedAtUtc = revokedAt
        });
        var command = environment.NewCreateCommand("revoked-project");
        await environment.Store.RegisterCommandAsync(command);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => environment.Store.CreateTaskAsync(environment.NewTask(), command.Id));
    }

    [Fact]
    public async Task RejectsTaskWorkingDirectoryOutsideAuthorizedProject()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var command = environment.NewCreateCommand("path-traversal");
        await environment.Store.RegisterCommandAsync(command);
        var task = environment.NewTask() with { WorkingDirectoryRelativePath = ".." };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => environment.Store.CreateTaskAsync(task, command.Id));
    }

    [Fact]
    public async Task CancellationIsPersistedAuditedAndSignalsRuntimeToken()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var (task, _) = await environment.CreateTaskAsync();
        await environment.Store.TransitionTaskAsync(
            task.Id,
            AgentTaskStatus.Running,
            TaskEventSource.System,
            "Executor started.");
        using var registry = new TaskCancellationRegistry();
        var token = registry.GetOrCreateToken(task.Id);
        var service = new TaskCancellationService(environment.Store, registry);

        var accepted = await service.RequestAsync(task.Id, environment.Device.Id);
        var persisted = await environment.Store.GetTaskAsync(task.Id);
        var events = await environment.Store.GetTaskEventsAsync(task.Id);
        var audit = await environment.Store.GetAuditLogAsync();

        Assert.True(accepted);
        Assert.True(token.IsCancellationRequested);
        Assert.NotNull(persisted);
        Assert.Equal(AgentTaskStatus.CancellationRequested, persisted.Status);
        Assert.NotNull(persisted.CancellationRequestedAtUtc);
        Assert.Equal(TaskEventType.CancellationRequested, events[^1].EventType);
        Assert.Contains(audit, entry =>
            entry.Action == "TaskCancellationRequested" && entry.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task RecoveryMarksUnfinishedTasksInterruptedWithoutRestartingThem()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var (task, _) = await environment.CreateTaskAsync();
        await environment.Store.TransitionTaskAsync(
            task.Id,
            AgentTaskStatus.Running,
            TaskEventSource.System,
            "Executor started.");
        var recoveredAt = DateTimeOffset.UtcNow.AddMinutes(1);
        var service = new TaskRecoveryService(environment.Store);

        var result = await service.RecoverAsync(recoveredAt);
        var secondRecovery = await service.RecoverAsync(recoveredAt.AddMinutes(1));
        var persisted = await environment.Store.GetTaskAsync(task.Id);
        var events = await environment.Store.GetTaskEventsAsync(task.Id);

        Assert.Contains(task.Id, result.InterruptedTaskIds);
        Assert.Empty(secondRecovery.InterruptedTaskIds);
        Assert.NotNull(persisted);
        Assert.Equal(AgentTaskStatus.Interrupted, persisted.Status);
        Assert.Equal(recoveredAt, persisted.UpdatedAtUtc);
        Assert.Equal(TaskEventType.RecoveryDetected, events[^1].EventType);
        Assert.Equal(TaskEventSource.Recovery, events[^1].Source);
        Assert.Equal(TaskPhase.Verifying, persisted.Phase);
        Assert.Equal(recoveredAt, persisted.CompletedAtUtc);
    }

    [Fact]
    public async Task TerminalTaskCannotBeChangedOrCancelled()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var (task, _) = await environment.CreateTaskAsync();
        await environment.Store.TransitionTaskAsync(
            task.Id,
            AgentTaskStatus.Running,
            TaskEventSource.System,
            "Executor started.");
        await environment.Store.TransitionTaskAsync(
            task.Id,
            AgentTaskStatus.Succeeded,
            TaskEventSource.Agent,
            "Executor reported success.");

        var cancellationAccepted = await environment.Store.RequestCancellationAsync(
            task.Id,
            environment.Device.Id);

        Assert.False(cancellationAccepted);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => environment.Store.TransitionTaskAsync(
                task.Id,
                AgentTaskStatus.Running,
                TaskEventSource.System,
                "Invalid restart."));
    }

    [Fact]
    public async Task AgentEventIsIdempotentAtSqliteBoundary()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var (task, _) = await environment.CreateTaskAsync();
        var now = DateTimeOffset.UtcNow;
        var (run, attempt) = NewAgentAttempt(task, now);
        await environment.Store.CreateAgentAttemptAsync(run, attempt);
        await environment.Store.RecordAgentStartedAsync(
            task.Id,
            run.Id,
            attempt.Id,
            "thread-test-id",
            "codex-cli 0.151.0",
            1234,
            now);
        var request = new AgentEventApplyRequest
        {
            TaskId = task.Id,
            AgentRunId = run.Id,
            AttemptId = attempt.Id,
            SequenceNumber = 2,
            ExternalEventId = "same-terminal-event",
            EventKind = "Completed",
            RunStatus = AgentRunStatus.Succeeded,
            AttemptStatus = AgentAttemptStatus.Completed,
            TaskStatus = AgentTaskStatus.Succeeded,
            OccurredAtUtc = now.AddSeconds(1),
            Message = "Authoritative turn.completed received.",
            FinalSummary = "done",
            FinalResultJson = "{\"outcome\":\"completed\"}",
            ExitCode = 0
        };

        var first = await environment.Store.ApplyAgentEventAsync(request);
        var duplicate = await environment.Store.ApplyAgentEventAsync(request);
        var events = await environment.Store.GetTaskEventsAsync(task.Id);

        Assert.True(first.Applied);
        Assert.False(duplicate.Applied);
        Assert.Single(events, item => item.ToStatus == AgentTaskStatus.Succeeded);
        Assert.Equal(AgentTaskStatus.Succeeded, duplicate.Task.Status);
    }

    [Fact]
    public async Task RecoveryInterruptsAgentRunAndAttemptButPreservesThreadId()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var (task, _) = await environment.CreateTaskAsync();
        var now = DateTimeOffset.UtcNow;
        var (run, attempt) = NewAgentAttempt(task, now);
        await environment.Store.CreateAgentAttemptAsync(run, attempt);
        await environment.Store.RecordAgentStartedAsync(
            task.Id,
            run.Id,
            attempt.Id,
            "thread-preserved-after-restart",
            "codex-cli 0.151.0",
            4321,
            now);
        var invocation = new SkillInvocationRecord
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            SequenceNumber = 1,
            SkillId = "codex.project-task",
            SkillVersion = "0.2.0",
            Capability = "coding.execute",
            InputJson = "{\"instruction\":\"test\"}",
            Status = SkillInvocationStatus.Running,
            CreatedAtUtc = now,
            StartedAtUtc = now
        };
        await environment.Store.UpsertSkillInvocationAsync(invocation);

        var recovered = await environment.Store.RecoverInterruptedTasksAsync(now.AddMinutes(1));
        var persistedRun = await environment.Store.GetAgentRunByTaskAsync(task.Id);
        var persistedAttempt = Assert.Single(await environment.Store.GetAgentAttemptsAsync(task.Id));

        Assert.Contains(task.Id, recovered.InterruptedTaskIds);
        Assert.Equal("thread-preserved-after-restart", persistedRun?.ExternalRunId);
        Assert.Equal(AgentRunStatus.Interrupted, persistedRun?.Status);
        Assert.Equal(AgentAttemptStatus.Interrupted, persistedAttempt.Status);
        Assert.Equal(AgentTaskStatus.Interrupted, (await environment.Store.GetTaskAsync(task.Id))?.Status);
        var persistedInvocation = Assert.Single(
            await environment.Store.GetSkillInvocationsAsync(task.Id));
        Assert.Equal(SkillInvocationStatus.Interrupted, persistedInvocation.Status);
        Assert.Equal("host_restarted", persistedInvocation.FailureCode);
    }

    [Fact]
    public async Task TaskEvidencePersistsAcrossStoreInstancesAndIsAudited()
    {
        await using var environment = await TaskStoreTestEnvironment.CreateAsync();
        var (task, _) = await environment.CreateTaskAsync();
        var now = DateTimeOffset.UtcNow;
        var evidence = new TaskEvidence
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            GeneratedAtUtc = now,
            TaskStatus = AgentTaskStatus.Succeeded,
            AgentClaim = new AgentClaimEvidence
            {
                Status = AgentClaimStatus.Completed,
                FinalExplanation = "done",
                ClaimedChangedFiles = ["a.cs"],
                ClaimedTests = ["dotnet test:passed"]
            },
            Git = new GitTaskEvidence
            {
                IsGitRepository = true,
                BeforeCapturedAtUtc = now.AddMinutes(-1),
                AfterCapturedAtUtc = now,
                PreExistingChangedFiles = [],
                BeforeStatus = [],
                AfterStatus = [],
                ChangedFiles =
                [
                    new EvidenceFileChange
                    {
                        RelativePath = "a.cs",
                        ChangeType = EvidenceFileChangeType.Modified,
                        AddedLines = 2,
                        DeletedLines = 1
                    }
                ],
                ModifiedFileCount = 1,
                AddedLineCount = 2,
                DeletedLineCount = 1,
                DiffStatVerified = true
            },
            Tests = new TaskTestEvidence
            {
                Status = EvidenceTestStatus.Passed,
                Commands =
                [
                    new TestCommandEvidence
                    {
                        Command = "dotnet test",
                        ExternalItemId = "test-1",
                        ExitCode = 0,
                        Status = EvidenceTestStatus.Passed,
                        TotalTests = 12,
                        PassedTests = 12,
                        FailedTests = 0,
                        SkippedTests = 0
                    }
                ],
                TotalTests = 12,
                PassedTests = 12,
                FailedTests = 0,
                SkippedTests = 0,
                HasRealExecutionEvidence = true
            },
            Connector = new ConnectorCompatibilityEvidence
            {
                ConnectorId = "codex",
                DetectedVersion = "0.147.0",
                VersionVerified = true,
                VerifiedVersions = ["0.147.0"],
                Decision = "allowed"
            },
            VerificationStatus = EvidenceVerificationStatus.Verified,
            VerificationReasons = [],
            UserSummary = "Codex 已完成任务，修改 1 个文件。执行 12 项测试，全部通过。"
        };

        await environment.Store.UpsertTaskEvidenceAsync(evidence);
        await using var reopened = new SqliteTaskStore(environment.DatabasePath);
        await reopened.InitializeAsync();
        var persisted = await reopened.GetTaskEvidenceAsync(task.Id);
        var audit = await reopened.GetAuditLogAsync();

        Assert.NotNull(persisted);
        Assert.Equal(EvidenceVerificationStatus.Verified, persisted.VerificationStatus);
        Assert.Equal("0.147.0", persisted.Connector.DetectedVersion);
        Assert.Equal(12, persisted.Tests.TotalTests);
        Assert.Equal("a.cs", Assert.Single(persisted.Git.ChangedFiles).RelativePath);
        Assert.Equal(evidence.UserSummary, persisted.UserSummary);
        Assert.Contains(audit, item =>
            item.Action == "TaskEvidenceRecorded" && item.EntityId == task.Id.ToString("D"));
    }

    private static async Task<string[]> ReadSessionTurnColumnsAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('session_turns') ORDER BY cid;";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        return columns.ToArray();
    }

    private static async Task<JsonElement> RunStage1CompatibilityScenarioAsync(string scenario)
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "scripts",
            "test-stage1-pre-v8-rollback.ps1");
        Assert.True(
            File.Exists(scriptPath),
            "尚未证明精确 Stage1 能读取 pre-v8 备份并拒绝 v8：验证脚本入口不存在。");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(scriptPath);
        process.StartInfo.ArgumentList.Add("-RepositoryRoot");
        process.StartInfo.ArgumentList.Add(repositoryRoot);
        process.StartInfo.ArgumentList.Add("-Scenario");
        process.StartInfo.ArgumentList.Add(scenario);
        Assert.True(process.Start(), "无法启动 Stage1 数据库兼容验证脚本。");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
        var output = await standardOutput;
        _ = await standardError;
        var resultLine = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(line => line.StartsWith(
                "STAGE1_PRE_V8_RESULT ",
                StringComparison.Ordinal));
        Assert.False(
            string.IsNullOrWhiteSpace(resultLine),
            "精确 Stage1 数据库兼容验证没有返回脱敏结果摘要。");
        using var document = JsonDocument.Parse(
            resultLine["STAGE1_PRE_V8_RESULT ".Length..]);
        var result = document.RootElement.Clone();
        Assert.True(
            process.ExitCode == 0,
            $"精确 Stage1 数据库兼容验证失败：{result.GetProperty("errorCode").GetString()}。");
        return result;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ScreenGuide.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName
               ?? throw new DirectoryNotFoundException("找不到仓库根目录。");
    }

    private static (AgentRunRecord Run, AgentAttemptRecord Attempt) NewAgentAttempt(
        AgentTask task,
        DateTimeOffset now)
    {
        var run = new AgentRunRecord
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            ConnectorId = "codex",
            Transport = "exec-json-v1",
            Status = AgentRunStatus.Starting,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var attempt = new AgentAttemptRecord
        {
            Id = Guid.NewGuid(),
            AgentRunId = run.Id,
            TaskId = task.Id,
            AttemptNumber = 1,
            Operation = AgentAttemptOperation.Start,
            Status = AgentAttemptStatus.Starting,
            StartedAtUtc = now,
            InputHash = "test-input-hash"
        };
        return (run, attempt);
    }
}
