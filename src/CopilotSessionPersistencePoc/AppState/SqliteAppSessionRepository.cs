using System.Globalization;
using CopilotSessionPersistencePoc.Persistence;
using Microsoft.Data.Sqlite;

namespace CopilotSessionPersistencePoc.AppState;

public sealed class SqliteAppSessionRepository(ISqliteConnectionFactory connectionFactory)
    : IAppSessionRepository
{
    public async Task<IReadOnlyList<AppSession>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, model, is_initialized, created_at, updated_at, version
            FROM app_sessions
            ORDER BY updated_at DESC;
            """;

        var sessions = new List<AppSession>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sessions.Add(ReadSession(reader));
        }

        return sessions;
    }

    public async Task<AppSession?> GetAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, model, is_initialized, created_at, updated_at, version
            FROM app_sessions
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadSession(reader)
            : null;
    }

    public async Task<bool> ExistsForDeletionAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        await GetAsync(id, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<AppSession> CreateAsync(
        string id,
        string title,
        string model,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection =
            await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_sessions (
                id, title, model, is_initialized, created_at, updated_at, version
            )
            VALUES ($id, $title, $model, 0, $createdAt, $updatedAt, 0);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(now));
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new AppSession(id, title, model, false, now, now, 0);
    }

    public async Task<AppSession> MarkInitializedAsync(
        string id,
        long expectedVersion,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        await UpdateVersionedAsync(
                id,
                expectedVersion,
                markInitialized: true,
                title,
                cancellationToken)
            .ConfigureAwait(false);

        return await GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session '{id}' no longer exists.");
    }

    public Task TouchAsync(
        string id,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        UpdateVersionedAsync(
            id,
            expectedVersion,
            markInitialized: false,
            title: null,
            cancellationToken);

    public async Task DeleteAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var fsCommand = connection.CreateCommand())
        {
            fsCommand.Transaction = transaction;
            fsCommand.CommandText = "DELETE FROM session_fs_nodes WHERE session_id = $id;";
            fsCommand.Parameters.AddWithValue("$id", id);
            await fsCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var sessionCommand = connection.CreateCommand())
        {
            sessionCommand.Transaction = transaction;
            sessionCommand.CommandText = "DELETE FROM app_sessions WHERE id = $id;";
            sessionCommand.Parameters.AddWithValue("$id", id);
            await sessionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateVersionedAsync(
        string id,
        long expectedVersion,
        bool markInitialized,
        string? title,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE app_sessions
            SET is_initialized = CASE
                    WHEN $markInitialized = 1 THEN 1
                    ELSE is_initialized
                END,
                title = COALESCE($title, title),
                updated_at = $updatedAt,
                version = version + 1
            WHERE id = $id AND version = $expectedVersion;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        command.Parameters.AddWithValue("$markInitialized", markInitialized ? 1 : 0);
        command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(DateTimeOffset.UtcNow));

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new SessionConcurrencyException(id);
        }
    }

    private static AppSession ReadSession(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3) != 0,
            ParseTimestamp(reader.GetString(4)),
            ParseTimestamp(reader.GetString(5)),
            reader.GetInt64(6));

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
