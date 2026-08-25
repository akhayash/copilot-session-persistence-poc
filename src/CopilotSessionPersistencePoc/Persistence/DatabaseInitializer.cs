namespace CopilotSessionPersistencePoc.Persistence;

public sealed class DatabaseInitializer(ISqliteConnectionFactory connectionFactory)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection =
            await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS schema_info (
                version INTEGER NOT NULL
            );

            INSERT INTO schema_info (version)
            SELECT 1
            WHERE NOT EXISTS (SELECT 1 FROM schema_info);

            CREATE TABLE IF NOT EXISTS app_sessions (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                model TEXT NOT NULL,
                is_initialized INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                version INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS session_fs_nodes (
                session_id TEXT NOT NULL,
                path TEXT NOT NULL,
                kind TEXT NOT NULL CHECK (kind IN ('file', 'directory')),
                content TEXT,
                mode INTEGER,
                birthtime TEXT NOT NULL,
                mtime TEXT NOT NULL,
                version INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (session_id, path)
            );

            CREATE INDEX IF NOT EXISTS ix_session_fs_nodes_session_path
                ON session_fs_nodes (session_id, path);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
