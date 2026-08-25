using CopilotSessionPersistencePoc.Persistence;
using GitHub.Copilot;
using Microsoft.Data.Sqlite;

namespace CopilotSessionPersistencePoc.SessionFs;

public sealed class SqliteSessionFsProviderFactory(ISqliteConnectionFactory connectionFactory)
    : ISessionFsProviderFactory
{
    public SessionFsProvider Create(string sessionId) =>
        new SqliteSessionFsProvider(connectionFactory, sessionId);

    public async Task<bool> HasSessionStateAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection =
            await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM session_fs_nodes
                WHERE session_id = $sessionId
            );
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        object? result =
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long value && value != 0;
    }
}
