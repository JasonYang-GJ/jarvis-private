namespace ScreenGuide.Persistence.Sqlite;

internal static class SqliteSchema
{
    public const string CreateVersion1 = """
        PRAGMA foreign_keys = ON;
        PRAGMA journal_mode = WAL;
        PRAGMA busy_timeout = 5000;

        CREATE TABLE IF NOT EXISTS schema_info (
            version INTEGER NOT NULL PRIMARY KEY,
            applied_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS devices (
            id TEXT NOT NULL PRIMARY KEY,
            display_name TEXT NOT NULL,
            device_type TEXT NOT NULL,
            trust_state TEXT NOT NULL,
            public_key_thumbprint TEXT NULL,
            created_at_utc TEXT NOT NULL,
            last_seen_at_utc TEXT NULL,
            revoked_at_utc TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS projects (
            id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            root_path TEXT NOT NULL COLLATE NOCASE UNIQUE,
            authorization_state TEXT NOT NULL,
            authorized_by_device_id TEXT NOT NULL,
            authorized_at_utc TEXT NOT NULL,
            revoked_at_utc TEXT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            FOREIGN KEY (authorized_by_device_id) REFERENCES devices(id)
        );

        CREATE TABLE IF NOT EXISTS tasks (
            id TEXT NOT NULL PRIMARY KEY,
            project_id TEXT NOT NULL,
            created_by_device_id TEXT NOT NULL,
            title TEXT NOT NULL,
            instruction TEXT NOT NULL,
            working_directory_relative_path TEXT NOT NULL,
            executor TEXT NOT NULL,
            status TEXT NOT NULL,
            cancellation_requested_at_utc TEXT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            started_at_utc TEXT NULL,
            completed_at_utc TEXT NULL,
            last_heartbeat_at_utc TEXT NULL,
            failure_code TEXT NULL,
            failure_message TEXT NULL,
            version INTEGER NOT NULL,
            FOREIGN KEY (project_id) REFERENCES projects(id),
            FOREIGN KEY (created_by_device_id) REFERENCES devices(id)
        );

        CREATE TABLE IF NOT EXISTS commands (
            id TEXT NOT NULL PRIMARY KEY,
            source_device_id TEXT NOT NULL,
            project_id TEXT NULL,
            task_id TEXT NULL,
            idempotency_key TEXT NOT NULL,
            command_type TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            received_at_utc TEXT NOT NULL,
            expires_at_utc TEXT NULL,
            status TEXT NOT NULL,
            processed_at_utc TEXT NULL,
            rejection_reason TEXT NULL,
            FOREIGN KEY (source_device_id) REFERENCES devices(id),
            FOREIGN KEY (project_id) REFERENCES projects(id),
            FOREIGN KEY (task_id) REFERENCES tasks(id),
            UNIQUE (source_device_id, idempotency_key)
        );

        CREATE TABLE IF NOT EXISTS task_events (
            id TEXT NOT NULL PRIMARY KEY,
            task_id TEXT NOT NULL,
            sequence_number INTEGER NOT NULL,
            event_type TEXT NOT NULL,
            from_status TEXT NULL,
            to_status TEXT NULL,
            source TEXT NOT NULL,
            source_device_id TEXT NULL,
            command_id TEXT NULL,
            occurred_at_utc TEXT NOT NULL,
            message TEXT NOT NULL,
            data_json TEXT NULL,
            FOREIGN KEY (task_id) REFERENCES tasks(id) ON DELETE CASCADE,
            FOREIGN KEY (source_device_id) REFERENCES devices(id),
            FOREIGN KEY (command_id) REFERENCES commands(id),
            UNIQUE (task_id, sequence_number)
        );

        CREATE TABLE IF NOT EXISTS audit_log (
            id TEXT NOT NULL PRIMARY KEY,
            occurred_at_utc TEXT NOT NULL,
            actor_device_id TEXT NULL,
            action TEXT NOT NULL,
            entity_type TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            outcome TEXT NOT NULL,
            details_json TEXT NULL,
            FOREIGN KEY (actor_device_id) REFERENCES devices(id)
        );

        CREATE INDEX IF NOT EXISTS ix_tasks_project_status ON tasks(project_id, status);
        CREATE INDEX IF NOT EXISTS ix_task_events_task_sequence ON task_events(task_id, sequence_number);
        CREATE INDEX IF NOT EXISTS ix_commands_task ON commands(task_id);
        CREATE INDEX IF NOT EXISTS ix_audit_log_time ON audit_log(occurred_at_utc);
        """;

    public const string CreateVersion2 = """
        CREATE TABLE IF NOT EXISTS agent_runs (
            id TEXT NOT NULL PRIMARY KEY,
            task_id TEXT NOT NULL UNIQUE,
            connector_id TEXT NOT NULL,
            transport TEXT NOT NULL,
            external_run_id TEXT NULL,
            connector_version TEXT NULL,
            status TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            last_event_sequence INTEGER NOT NULL,
            last_event_type TEXT NULL,
            last_event_at_utc TEXT NULL,
            final_summary TEXT NULL,
            final_result_json TEXT NULL,
            failure_code TEXT NULL,
            failure_message TEXT NULL,
            FOREIGN KEY (task_id) REFERENCES tasks(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS agent_attempts (
            id TEXT NOT NULL PRIMARY KEY,
            agent_run_id TEXT NOT NULL,
            task_id TEXT NOT NULL,
            command_id TEXT NULL,
            attempt_number INTEGER NOT NULL,
            operation TEXT NOT NULL,
            status TEXT NOT NULL,
            process_id INTEGER NULL,
            process_started_at_utc TEXT NULL,
            started_at_utc TEXT NOT NULL,
            terminal_event_at_utc TEXT NULL,
            process_exited_at_utc TEXT NULL,
            terminal_event_type TEXT NULL,
            exit_code INTEGER NULL,
            cancellation_requested_at_utc TEXT NULL,
            cancellation_confirmed_at_utc TEXT NULL,
            last_event_sequence INTEGER NOT NULL,
            last_event_at_utc TEXT NULL,
            input_hash TEXT NOT NULL,
            FOREIGN KEY (agent_run_id) REFERENCES agent_runs(id) ON DELETE CASCADE,
            FOREIGN KEY (task_id) REFERENCES tasks(id) ON DELETE CASCADE,
            FOREIGN KEY (command_id) REFERENCES commands(id),
            UNIQUE (agent_run_id, attempt_number)
        );

        CREATE TABLE IF NOT EXISTS agent_connector_events (
            id TEXT NOT NULL PRIMARY KEY,
            agent_run_id TEXT NOT NULL,
            attempt_id TEXT NOT NULL,
            task_id TEXT NOT NULL,
            sequence_number INTEGER NOT NULL,
            external_event_id TEXT NOT NULL,
            event_kind TEXT NOT NULL,
            run_status TEXT NOT NULL,
            occurred_at_utc TEXT NOT NULL,
            message TEXT NOT NULL,
            data_json TEXT NULL,
            FOREIGN KEY (agent_run_id) REFERENCES agent_runs(id) ON DELETE CASCADE,
            FOREIGN KEY (attempt_id) REFERENCES agent_attempts(id) ON DELETE CASCADE,
            FOREIGN KEY (task_id) REFERENCES tasks(id) ON DELETE CASCADE,
            UNIQUE (attempt_id, external_event_id)
        );

        CREATE TABLE IF NOT EXISTS decision_requests (
            id TEXT NOT NULL PRIMARY KEY,
            task_id TEXT NOT NULL,
            agent_run_id TEXT NOT NULL,
            attempt_id TEXT NOT NULL,
            question TEXT NOT NULL,
            options_json TEXT NOT NULL,
            status TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            responded_at_utc TEXT NULL,
            response_command_id TEXT NULL,
            FOREIGN KEY (task_id) REFERENCES tasks(id) ON DELETE CASCADE,
            FOREIGN KEY (agent_run_id) REFERENCES agent_runs(id) ON DELETE CASCADE,
            FOREIGN KEY (attempt_id) REFERENCES agent_attempts(id) ON DELETE CASCADE,
            FOREIGN KEY (response_command_id) REFERENCES commands(id)
        );

        CREATE INDEX IF NOT EXISTS ix_agent_runs_status ON agent_runs(status);
        CREATE INDEX IF NOT EXISTS ix_agent_attempts_task ON agent_attempts(task_id, attempt_number);
        CREATE INDEX IF NOT EXISTS ix_agent_events_task ON agent_connector_events(task_id, sequence_number);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_pending_decision_per_task
            ON decision_requests(task_id)
            WHERE status = 'Pending';
        """;

    public const string CreateVersion3 = """
        CREATE TABLE IF NOT EXISTS task_evidence (
            id TEXT NOT NULL PRIMARY KEY,
            task_id TEXT NOT NULL UNIQUE,
            generated_at_utc TEXT NOT NULL,
            verification_status TEXT NOT NULL,
            user_summary TEXT NOT NULL,
            evidence_json TEXT NOT NULL,
            FOREIGN KEY (task_id) REFERENCES tasks(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_task_evidence_verification
            ON task_evidence(verification_status, generated_at_utc);
        """;

    public const string CreateVersion4 = """
        ALTER TABLE tasks ADD COLUMN phase TEXT NOT NULL DEFAULT 'Planning';
        ALTER TABLE task_events ADD COLUMN from_phase TEXT NULL;
        ALTER TABLE task_events ADD COLUMN to_phase TEXT NULL;

        UPDATE tasks
        SET phase = CASE status
            WHEN 'Pending' THEN 'Planning'
            WHEN 'WaitingForUser' THEN 'AwaitingPermission'
            WHEN 'Running' THEN 'Executing'
            WHEN 'CancellationRequested' THEN 'Executing'
            ELSE 'Verifying'
        END;

        CREATE TABLE project_authorizations (
            id TEXT NOT NULL PRIMARY KEY,
            project_id TEXT NOT NULL UNIQUE,
            scope TEXT NOT NULL,
            scope_value TEXT NOT NULL,
            state TEXT NOT NULL,
            authorized_by_device_id TEXT NOT NULL,
            authorized_at_utc TEXT NOT NULL,
            revoked_at_utc TEXT NULL,
            updated_at_utc TEXT NOT NULL,
            FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE,
            FOREIGN KEY (authorized_by_device_id) REFERENCES devices(id)
        );

        INSERT INTO project_authorizations(
            id, project_id, scope, scope_value, state,
            authorized_by_device_id, authorized_at_utc, revoked_at_utc, updated_at_utc)
        SELECT
            id, id, 'ProjectDirectory', root_path, authorization_state,
            authorized_by_device_id, authorized_at_utc, revoked_at_utc, updated_at_utc
        FROM projects;

        CREATE TABLE resource_scopes (
            id TEXT NOT NULL PRIMARY KEY,
            task_id TEXT NOT NULL,
            scope_type TEXT NOT NULL,
            resource_id TEXT NULL,
            scope_value TEXT NOT NULL,
            access_mode TEXT NOT NULL,
            granted_by_device_id TEXT NOT NULL,
            granted_at_utc TEXT NOT NULL,
            expires_at_utc TEXT NULL,
            revoked_at_utc TEXT NULL,
            FOREIGN KEY (task_id) REFERENCES tasks(id) ON DELETE CASCADE,
            FOREIGN KEY (granted_by_device_id) REFERENCES devices(id),
            UNIQUE (task_id, scope_type, resource_id, scope_value)
        );

        INSERT INTO resource_scopes(
            id, task_id, scope_type, resource_id, scope_value, access_mode,
            granted_by_device_id, granted_at_utc, expires_at_utc, revoked_at_utc)
        SELECT
            lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' ||
            substr(lower(hex(randomblob(2))), 2) || '-' ||
            substr('89ab', abs(random()) % 4 + 1, 1) ||
            substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6))),
            tasks.id, 'Project', tasks.project_id, projects.root_path,
            'Execute', tasks.created_by_device_id, tasks.created_at_utc, NULL, NULL
        FROM tasks
        INNER JOIN projects ON projects.id = tasks.project_id;

        CREATE TABLE skill_invocations (
            id TEXT NOT NULL PRIMARY KEY,
            task_id TEXT NOT NULL,
            sequence_number INTEGER NOT NULL,
            skill_id TEXT NOT NULL,
            skill_version TEXT NOT NULL,
            capability TEXT NOT NULL,
            input_json TEXT NOT NULL,
            status TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            started_at_utc TEXT NULL,
            completed_at_utc TEXT NULL,
            failure_code TEXT NULL,
            failure_message TEXT NULL,
            FOREIGN KEY (task_id) REFERENCES tasks(id) ON DELETE CASCADE,
            UNIQUE (task_id, sequence_number)
        );

        CREATE INDEX ix_project_authorizations_state
            ON project_authorizations(state, updated_at_utc);
        CREATE INDEX ix_resource_scopes_task
            ON resource_scopes(task_id, scope_type);
        CREATE INDEX ix_skill_invocations_task
            ON skill_invocations(task_id, sequence_number);
        """;
}
