using System.Globalization;
using System.Text;
using CopilotSessionPersistencePoc.Persistence;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Data.Sqlite;

namespace CopilotSessionPersistencePoc.SessionFs;

public sealed class SqliteSessionFsProvider(
    ISqliteConnectionFactory connectionFactory,
    string sessionId)
    : SessionFsProvider
{
    protected override async Task<string> ReadFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var normalized = SessionFsPath.Parse(path);
        var node = await GetNodeAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (node is null || node.Kind != NodeKind.File)
        {
            throw new FileNotFoundException($"SessionFS file '{normalized}' does not exist.");
        }

        return node.Content ?? string.Empty;
    }

    protected override async Task WriteFileAsync(
        string path,
        string content,
        int? mode,
        CancellationToken cancellationToken)
    {
        var normalized = SessionFsPath.Parse(path);
        if (normalized.Value == "/")
        {
            throw new IOException("The SessionFS root cannot be written as a file.");
        }

        await using var connection =
            await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureAncestorsAsync(connection, transaction, normalized, mode, cancellationToken)
            .ConfigureAwait(false);

        var existing = await GetNodeAsync(connection, transaction, normalized, cancellationToken)
            .ConfigureAwait(false);
        if (existing?.Kind == NodeKind.Directory)
        {
            throw new IOException($"SessionFS path '{normalized}' is a directory.");
        }

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_fs_nodes (
                session_id, path, kind, content, mode, birthtime, mtime, version
            )
            VALUES ($sessionId, $path, 'file', $content, $mode, $now, $now, 0)
            ON CONFLICT(session_id, path) DO UPDATE SET
                content = excluded.content,
                mode = COALESCE(excluded.mode, session_fs_nodes.mode),
                mtime = excluded.mtime,
                version = session_fs_nodes.version + 1;
            """;
        AddNodeParameters(command, normalized, content, mode, now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task AppendFileAsync(
        string path,
        string content,
        int? mode,
        CancellationToken cancellationToken)
    {
        var normalized = SessionFsPath.Parse(path);
        if (normalized.Value == "/")
        {
            throw new IOException("The SessionFS root cannot be written as a file.");
        }

        await using var connection =
            await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureAncestorsAsync(connection, transaction, normalized, mode, cancellationToken)
            .ConfigureAwait(false);

        var existing = await GetNodeAsync(connection, transaction, normalized, cancellationToken)
            .ConfigureAwait(false);
        if (existing?.Kind == NodeKind.Directory)
        {
            throw new IOException($"SessionFS path '{normalized}' is a directory.");
        }

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_fs_nodes (
                session_id, path, kind, content, mode, birthtime, mtime, version
            )
            VALUES ($sessionId, $path, 'file', $content, $mode, $now, $now, 0)
            ON CONFLICT(session_id, path) DO UPDATE SET
                content = COALESCE(session_fs_nodes.content, '') || excluded.content,
                mode = COALESCE(session_fs_nodes.mode, excluded.mode),
                mtime = excluded.mtime,
                version = session_fs_nodes.version + 1;
            """;
        AddNodeParameters(command, normalized, content, mode, now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task<bool> ExistsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var normalized = SessionFsPath.Parse(path);
        if (normalized.Value == "/")
        {
            return true;
        }

        return await GetNodeAsync(normalized, cancellationToken).ConfigureAwait(false) is not null;
    }

    protected override async Task<SessionFsStatResult> StatAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var normalized = SessionFsPath.Parse(path);
        if (normalized.Value == "/")
        {
            return new SessionFsStatResult
            {
                IsDirectory = true,
                IsFile = false,
                Size = 0,
                Birthtime = DateTimeOffset.UnixEpoch,
                Mtime = DateTimeOffset.UnixEpoch,
            };
        }

        var node = await GetNodeAsync(normalized, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"SessionFS path '{normalized}' does not exist.");
        var isFile = node.Kind == NodeKind.File;
        return new SessionFsStatResult
        {
            IsDirectory = !isFile,
            IsFile = isFile,
            Size = isFile ? Encoding.UTF8.GetByteCount(node.Content ?? string.Empty) : 0,
            Birthtime = node.Birthtime,
            Mtime = node.Mtime,
        };
    }

    protected override async Task MakeDirectoryAsync(
        string path,
        bool recursive,
        int? mode,
        CancellationToken cancellationToken)
    {
        var normalized = SessionFsPath.Parse(path);
        await using var connection =
            await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (recursive)
        {
            foreach (var ancestor in normalized.Ancestors())
            {
                await InsertDirectoryAsync(
                        connection,
                        transaction,
                        SessionFsPath.Parse(ancestor),
                        mode,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else if (normalized.Parent is { } parent && parent != "/")
        {
            var parentNode = await GetNodeAsync(
                    connection,
                    transaction,
                    SessionFsPath.Parse(parent),
                    cancellationToken)
                .ConfigureAwait(false);
            if (parentNode?.Kind != NodeKind.Directory)
            {
                throw new DirectoryNotFoundException(
                    $"Parent directory '{parent}' does not exist.");
            }
        }

        await InsertDirectoryAsync(connection, transaction, normalized, mode, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task<IList<string>> ReadDirectoryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var entries = await ReadChildrenAsync(path, cancellationToken).ConfigureAwait(false);
        return entries.Select(entry => entry.Name).ToArray();
    }

    protected override async Task<IList<SessionFsReaddirWithTypesEntry>>
        ReadDirectoryWithTypesAsync(
            string path,
            CancellationToken cancellationToken)
    {
        var entries = await ReadChildrenAsync(path, cancellationToken).ConfigureAwait(false);
        return entries
            .Select(entry => new SessionFsReaddirWithTypesEntry
            {
                Name = entry.Name,
                Type = entry.Kind == NodeKind.Directory
                    ? SessionFsReaddirWithTypesEntryType.Directory
                    : SessionFsReaddirWithTypesEntryType.File,
            })
            .ToArray();
    }

    protected override async Task RemoveAsync(
        string path,
        bool recursive,
        bool force,
        CancellationToken cancellationToken)
    {
        var normalized = SessionFsPath.Parse(path);
        if (normalized.Value == "/")
        {
            throw new IOException("The SessionFS root cannot be removed.");
        }

        await using var connection =
            await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var node = await GetNodeAsync(connection, transaction, normalized, cancellationToken)
            .ConfigureAwait(false);
        if (node is null)
        {
            if (force)
            {
                return;
            }

            throw new FileNotFoundException($"SessionFS path '{normalized}' does not exist.");
        }

        if (node.Kind == NodeKind.Directory && !recursive
            && await HasDescendantsAsync(connection, transaction, normalized, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new IOException($"SessionFS directory '{normalized}' is not empty.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = node.Kind == NodeKind.Directory
            ? """
                DELETE FROM session_fs_nodes
                WHERE session_id = $sessionId
                  AND (path = $path OR substr(path, 1, length($prefix)) = $prefix);
                """
            : """
                DELETE FROM session_fs_nodes
                WHERE session_id = $sessionId AND path = $path;
                """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$path", normalized.Value);
        command.Parameters.AddWithValue("$prefix", normalized.DescendantPrefix);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task RenameAsync(
        string src,
        string dest,
        CancellationToken cancellationToken)
    {
        var source = SessionFsPath.Parse(src);
        var destination = SessionFsPath.Parse(dest);
        if (source.Value == "/" || destination.Value == "/")
        {
            throw new IOException("The SessionFS root cannot be renamed.");
        }

        await using var connection =
            await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sourceNode = await GetNodeAsync(connection, transaction, source, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new FileNotFoundException($"SessionFS path '{source}' does not exist.");
        if (sourceNode.Kind == NodeKind.Directory
            && destination.Value.StartsWith(source.DescendantPrefix, StringComparison.Ordinal))
        {
            throw new IOException("A directory cannot be moved into its own subtree.");
        }

        if (await GetNodeAsync(connection, transaction, destination, cancellationToken)
                .ConfigureAwait(false) is not null)
        {
            throw new IOException($"Destination '{destination}' already exists.");
        }

        await EnsureAncestorsAsync(connection, transaction, destination, null, cancellationToken)
            .ConfigureAwait(false);

        var nodes = sourceNode.Kind == NodeKind.Directory
            ? await ReadSubtreeAsync(connection, transaction, source, cancellationToken)
                .ConfigureAwait(false)
            : [sourceNode];

        foreach (var node in nodes.OrderBy(item => item.Path.Length))
        {
            var suffix = node.Path == source.Value
                ? string.Empty
                : node.Path[source.Value.Length..];
            await InsertNodeAsync(
                    connection,
                    transaction,
                    node with { Path = destination.Value + suffix },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await using var deleteCommand = connection.CreateCommand();
        deleteCommand.Transaction = transaction;
        deleteCommand.CommandText = """
            DELETE FROM session_fs_nodes
            WHERE session_id = $sessionId
              AND (path = $path OR substr(path, 1, length($prefix)) = $prefix);
            """;
        deleteCommand.Parameters.AddWithValue("$sessionId", sessionId);
        deleteCommand.Parameters.AddWithValue("$path", source.Value);
        deleteCommand.Parameters.AddWithValue("$prefix", source.DescendantPrefix);
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Node>> ReadChildrenAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var normalized = SessionFsPath.Parse(path);
        if (normalized.Value != "/")
        {
            var directory = await GetNodeAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (directory?.Kind != NodeKind.Directory)
            {
                throw new DirectoryNotFoundException(
                    $"SessionFS directory '{normalized}' does not exist.");
            }
        }

        await using var connection =
            await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path, kind, content, mode, birthtime, mtime, version
            FROM session_fs_nodes
            WHERE session_id = $sessionId
              AND substr(path, 1, length($prefix)) = $prefix
            ORDER BY path;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$prefix", normalized.DescendantPrefix);

        var children = new Dictionary<string, Node>(StringComparer.Ordinal);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var node = ReadNode(reader);
            var remainder = node.Path[normalized.DescendantPrefix.Length..];
            if (remainder.Length == 0 || remainder.Contains('/', StringComparison.Ordinal))
            {
                continue;
            }

            children.TryAdd(remainder, node);
        }

        return children.Values.ToArray();
    }

    private async Task<Node?> GetNodeAsync(
        SessionFsPath path,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await GetNodeAsync(connection, null, path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Node?> GetNodeAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SessionFsPath path,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT path, kind, content, mode, birthtime, mtime, version
            FROM session_fs_nodes
            WHERE session_id = $sessionId AND path = $path;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$path", path.Value);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadNode(reader)
            : null;
    }

    private async Task EnsureAncestorsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionFsPath path,
        int? mode,
        CancellationToken cancellationToken)
    {
        foreach (var ancestor in path.Ancestors())
        {
            await InsertDirectoryAsync(
                    connection,
                    transaction,
                    SessionFsPath.Parse(ancestor),
                    mode,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task InsertDirectoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionFsPath path,
        int? mode,
        CancellationToken cancellationToken)
    {
        if (path.Value == "/")
        {
            return;
        }

        var existing = await GetNodeAsync(connection, transaction, path, cancellationToken)
            .ConfigureAwait(false);
        if (existing?.Kind == NodeKind.File)
        {
            throw new IOException($"SessionFS path '{path}' is a file.");
        }

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_fs_nodes (
                session_id, path, kind, content, mode, birthtime, mtime, version
            )
            VALUES ($sessionId, $path, 'directory', NULL, $mode, $now, $now, 0)
            ON CONFLICT(session_id, path) DO NOTHING;
            """;
        AddNodeParameters(command, path, null, mode, now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HasDescendantsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionFsPath path,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM session_fs_nodes
                WHERE session_id = $sessionId
                  AND substr(path, 1, length($prefix)) = $prefix
            );
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$prefix", path.DescendantPrefix);
        return Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) != 0;
    }

    private async Task<IReadOnlyList<Node>> ReadSubtreeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionFsPath path,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT path, kind, content, mode, birthtime, mtime, version
            FROM session_fs_nodes
            WHERE session_id = $sessionId
              AND (path = $path OR substr(path, 1, length($prefix)) = $prefix)
            ORDER BY length(path);
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$path", path.Value);
        command.Parameters.AddWithValue("$prefix", path.DescendantPrefix);

        var nodes = new List<Node>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            nodes.Add(ReadNode(reader));
        }

        return nodes;
    }

    private async Task InsertNodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Node node,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_fs_nodes (
                session_id, path, kind, content, mode, birthtime, mtime, version
            )
            VALUES ($sessionId, $path, $kind, $content, $mode, $birthtime, $mtime, $version);
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$path", node.Path);
        command.Parameters.AddWithValue("$kind", node.Kind == NodeKind.File ? "file" : "directory");
        command.Parameters.AddWithValue("$content", (object?)node.Content ?? DBNull.Value);
        command.Parameters.AddWithValue("$mode", (object?)node.Mode ?? DBNull.Value);
        command.Parameters.AddWithValue("$birthtime", FormatTimestamp(node.Birthtime));
        command.Parameters.AddWithValue("$mtime", FormatTimestamp(node.Mtime));
        command.Parameters.AddWithValue("$version", node.Version);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void AddNodeParameters(
        SqliteCommand command,
        SessionFsPath path,
        string? content,
        int? mode,
        string now)
    {
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$path", path.Value);
        command.Parameters.AddWithValue("$content", (object?)content ?? DBNull.Value);
        command.Parameters.AddWithValue("$mode", (object?)mode ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
    }

    private static Node ReadNode(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1) == "file" ? NodeKind.File : NodeKind.Directory,
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            ParseTimestamp(reader.GetString(4)),
            ParseTimestamp(reader.GetString(5)),
            reader.GetInt64(6));

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private enum NodeKind
    {
        File,
        Directory,
    }

    private sealed record Node(
        string Path,
        NodeKind Kind,
        string? Content,
        int? Mode,
        DateTimeOffset Birthtime,
        DateTimeOffset Mtime,
        long Version)
    {
        public string Name => SessionFsPath.Parse(Path).Name;
    }
}
