using System.Globalization;
using CopilotSessionPersistencePoc.Persistence;
using CopilotSessionPersistencePoc.SessionFs;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Diagnostics;

public sealed class SqliteSessionFsDiagnosticsReader(
    ISqliteConnectionFactory connectionFactory,
    IOptions<DiagnosticsOptions> options) : ISessionFsDiagnosticsReader
{
    public async Task<SessionFsDiagnosticsSnapshot> GetSnapshotAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection =
            await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SessionFsEntryInfo> entries =
            await ReadEntriesAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);
        int eventCount =
            await ReadEventCountAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);

        var databaseFile = new FileInfo(connectionFactory.DatabasePath);
        string hostSessionDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot",
            "session-state",
            sessionId);
        bool hostDirectoryExists = Directory.Exists(hostSessionDirectory);
        bool individualSessionFilesDetected = hostDirectoryExists
            && Directory.EnumerateFiles(
                    hostSessionDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .Any();
        var storage = new SessionFsStorageEvidence(
            "Sqlite",
            "SQLite table: app_sessions",
            "SQLite table: session_fs_nodes",
            "In-process session semaphore",
            "Not configured",
            "SQLite custom SessionFS provider",
            connectionFactory.DatabasePath,
            databaseFile.Exists,
            databaseFile.Exists ? databaseFile.Length : 0,
            hostSessionDirectory,
            hostDirectoryExists,
            individualSessionFilesDetected,
            individualSessionFilesDetected
                ? "Individual files were found under the matching host session directory; inspect them before claiming SQLite-only storage."
                : hostDirectoryExists
                    ? "A matching host session directory exists but contains no files. SessionFS content is materialized as SQLite rows."
                    : "No matching host session directory exists. SessionFS nodes and content are materialized as SQLite rows, not individual host files.");

        return new SessionFsDiagnosticsSnapshot(
            sessionId,
            entries.Count,
            entries.Count(static entry => entry.Kind == "file"),
            entries.Count(static entry => entry.Kind == "directory"),
            entries.Sum(static entry => entry.SizeBytes),
            eventCount,
            entries.Count == 0 ? null : entries.Max(static entry => entry.ModifiedTime),
            storage,
            entries);
    }

    public async Task<SessionFsEntryDetails?> GetEntryAsync(
        string sessionId,
        string path,
        CancellationToken cancellationToken = default)
    {
        SessionFsPath normalizedPath = SessionFsPath.Parse(path);
        int maximumCharacters = options.Value.MaximumPreviewCharacters;
        await using SqliteConnection connection =
            await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT path,
                   kind,
                   COALESCE(length(CAST(content AS BLOB)), 0),
                   birthtime,
                   mtime,
                   version,
                   substr(content, 1, $previewLength),
                   COALESCE(length(content), 0)
            FROM session_fs_nodes
            WHERE session_id = $sessionId AND path = $path;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$path", normalizedPath.Value);
        command.Parameters.AddWithValue("$previewLength", maximumCharacters + 1);

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var entry = ReadEntry(reader);
        int originalCharacterCount = reader.GetInt32(7);
        string? content = reader.IsDBNull(6) ? null : reader.GetString(6);
        bool truncated = content?.Length > maximumCharacters;
        if (truncated)
        {
            content = content![..maximumCharacters];
        }

        return new SessionFsEntryDetails(
            entry,
            content is null ? null : DiagnosticsContentRedactor.Redact(content, truncated),
            truncated,
            originalCharacterCount,
            "session_fs_nodes",
            sessionId,
            normalizedPath.Value);
    }

    private static async Task<IReadOnlyList<SessionFsEntryInfo>> ReadEntriesAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT path,
                   kind,
                   COALESCE(length(CAST(content AS BLOB)), 0),
                   birthtime,
                   mtime,
                   version
            FROM session_fs_nodes
            WHERE session_id = $sessionId
            ORDER BY path;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);

        var entries = new List<SessionFsEntryInfo>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(ReadEntry(reader));
        }

        return entries;
    }

    private static async Task<int> ReadEventCountAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT content
            FROM session_fs_nodes
            WHERE session_id = $sessionId
              AND path = '/session-state/events.jsonl'
              AND kind = 'file';
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is string content
            ? content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
            : 0;
    }

    private static SessionFsEntryInfo ReadEntry(SqliteDataReader reader) => new(
        reader.GetString(0),
        ClassifyPath(reader.GetString(0)),
        reader.GetString(1),
        reader.GetInt64(2),
        ParseTimestamp(reader.GetString(3)),
        ParseTimestamp(reader.GetString(4)),
        reader.GetInt64(5));

    private static string ClassifyPath(string path)
    {
        if (path.Equals("/session-state", StringComparison.Ordinal)
            || path.StartsWith("/session-state/", StringComparison.Ordinal))
        {
            return "canonical-session-state";
        }

        if (path.Length >= 4
            && path[0] == '/'
            && char.IsAsciiLetter(path[1])
            && path[2] == ':'
            && path[3] == '/')
        {
            return "host-shaped-virtual-key";
        }

        return "virtual";
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
