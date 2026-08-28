using CopilotSessionPersistencePoc.AppState;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Persistence;

public sealed class DatabaseInitializer(
    ISqliteConnectionFactory connectionFactory,
    IOptions<SessionOwnershipOptions>? ownershipOptions = null)
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
                owner_id TEXT NOT NULL,
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

        await using var schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = "PRAGMA table_info(app_sessions);";
        bool hasOwnerId = false;
        await using (var reader =
            await schemaCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetString(1).Equals("owner_id", StringComparison.OrdinalIgnoreCase))
                {
                    hasOwnerId = true;
                    break;
                }
            }
        }

        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!hasOwnerId)
        {
            await using var migration = connection.CreateCommand();
            migration.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
            migration.CommandText =
                "ALTER TABLE app_sessions ADD COLUMN owner_id TEXT NOT NULL DEFAULT '';";
            await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var backfillCommand = connection.CreateCommand();
        backfillCommand.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
        backfillCommand.CommandText = """
            UPDATE app_sessions
            SET owner_id = $ownerId
            WHERE owner_id = '';
            """;
        backfillCommand.Parameters.AddWithValue(
            "$ownerId",
            SessionOwnerKey.Create(
                ownershipOptions?.Value.LocalOwnerId ?? "local-user"));
        await backfillCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var indexCommand = connection.CreateCommand();
        indexCommand.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
        indexCommand.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_app_sessions_owner_updated
                ON app_sessions (owner_id, updated_at DESC);

            UPDATE schema_info SET version = 2;
            """;
        await indexCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
