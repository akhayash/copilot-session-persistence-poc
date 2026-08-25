using Microsoft.Data.Sqlite;

namespace CopilotSessionPersistencePoc.Persistence;

public interface ISqliteConnectionFactory
{
    Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}
