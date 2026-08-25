using Microsoft.Data.Sqlite;

namespace CopilotSessionPersistencePoc.Persistence;

public interface ISqliteConnectionFactory
{
    string DatabasePath { get; }

    Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}
