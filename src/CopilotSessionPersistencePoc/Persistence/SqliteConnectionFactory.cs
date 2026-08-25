using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Persistence;

public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly string connectionString;
    private readonly int busyTimeoutMilliseconds;

    public SqliteConnectionFactory(
        IOptions<PersistenceOptions> options,
        IHostEnvironment environment)
    {
        var configured = options.Value;
        DatabasePath = Path.GetFullPath(
            configured.DatabasePath,
            environment.ContentRootPath);

        Directory.CreateDirectory(
            Path.GetDirectoryName(DatabasePath)
                ?? throw new InvalidOperationException("The database path has no parent directory."));

        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        busyTimeoutMilliseconds = configured.BusyTimeoutMilliseconds;
    }

    public string DatabasePath { get; }

    public async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = {busyTimeoutMilliseconds};
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
