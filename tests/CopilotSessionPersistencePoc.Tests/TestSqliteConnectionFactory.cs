using CopilotSessionPersistencePoc.Persistence;
using Microsoft.Data.Sqlite;

namespace CopilotSessionPersistencePoc.Tests;

internal sealed class TestSqliteConnectionFactory : ISqliteConnectionFactory, IDisposable
{
    private readonly string directoryPath;
    private readonly string connectionString;

    public TestSqliteConnectionFactory()
    {
        directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"copilot-session-persistence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directoryPath, "test.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString();
    }

    public async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directoryPath, recursive: true);
    }
}
