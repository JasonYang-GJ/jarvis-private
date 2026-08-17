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
}
