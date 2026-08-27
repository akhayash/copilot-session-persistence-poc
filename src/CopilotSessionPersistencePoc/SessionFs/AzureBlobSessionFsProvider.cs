using System.Text;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace CopilotSessionPersistencePoc.SessionFs;

public sealed class AzureBlobSessionFsProvider(
    AzureBlobSessionFsStore store,
    string sessionId)
    : SessionFsProvider
{
    protected override async Task<string> ReadFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        SessionFsPath normalized = SessionFsPath.Parse(path);
        AzureSessionFsState state =
            await store.ReadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!state.Nodes.TryGetValue(normalized.Value, out AzureSessionFsNode? node)
            || node.Kind != "file")
        {
            throw new FileNotFoundException($"SessionFS file '{normalized}' does not exist.");
        }

        return node.Content ?? string.Empty;
    }

    protected override Task WriteFileAsync(
        string path,
        string content,
        int? mode,
        CancellationToken cancellationToken)
    {
        SessionFsPath normalized = RequireWritablePath(path);
        return store.MutateAsync(
            sessionId,
            state =>
            {
                EnsureAncestors(state, normalized, mode);
                if (state.Nodes.TryGetValue(normalized.Value, out AzureSessionFsNode? existing)
                    && existing.Kind == "directory")
                {
                    throw new IOException($"SessionFS path '{normalized}' is a directory.");
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                state.Nodes[normalized.Value] = existing is null
                    ? NewNode("file", content, mode, now)
                    : new AzureSessionFsNode
                    {
                        Kind = "file",
                        Content = content,
                        Mode = mode ?? existing.Mode,
                        Birthtime = existing.Birthtime,
                        Mtime = now,
                        Version = existing.Version + 1,
                    };
            },
            cancellationToken);
    }

    protected override Task AppendFileAsync(
        string path,
        string content,
        int? mode,
        CancellationToken cancellationToken)
    {
        SessionFsPath normalized = RequireWritablePath(path);
        return store.MutateAsync(
            sessionId,
            state =>
            {
                EnsureAncestors(state, normalized, mode);
                if (state.Nodes.TryGetValue(normalized.Value, out AzureSessionFsNode? existing)
                    && existing.Kind == "directory")
                {
                    throw new IOException($"SessionFS path '{normalized}' is a directory.");
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                state.Nodes[normalized.Value] = existing is null
                    ? NewNode("file", content, mode, now)
                    : new AzureSessionFsNode
                    {
                        Kind = "file",
                        Content = (existing.Content ?? string.Empty) + content,
                        Mode = existing.Mode ?? mode,
                        Birthtime = existing.Birthtime,
                        Mtime = now,
                        Version = existing.Version + 1,
                    };
            },
            cancellationToken);
    }

    protected override async Task<bool> ExistsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        SessionFsPath normalized = SessionFsPath.Parse(path);
        if (normalized.Value == "/")
        {
            return true;
        }

        AzureSessionFsState state =
            await store.ReadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return state.Nodes.ContainsKey(normalized.Value);
    }

    protected override async Task<SessionFsStatResult> StatAsync(
        string path,
        CancellationToken cancellationToken)
    {
        SessionFsPath normalized = SessionFsPath.Parse(path);
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

        AzureSessionFsState state =
            await store.ReadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!state.Nodes.TryGetValue(normalized.Value, out AzureSessionFsNode? node))
        {
            throw new FileNotFoundException($"SessionFS path '{normalized}' does not exist.");
        }

        bool isFile = node.Kind == "file";
        return new SessionFsStatResult
        {
            IsDirectory = !isFile,
            IsFile = isFile,
            Size = isFile ? Encoding.UTF8.GetByteCount(node.Content ?? string.Empty) : 0,
            Birthtime = node.Birthtime,
            Mtime = node.Mtime,
        };
    }

    protected override Task MakeDirectoryAsync(
        string path,
        bool recursive,
        int? mode,
        CancellationToken cancellationToken)
    {
        SessionFsPath normalized = SessionFsPath.Parse(path);
        return store.MutateAsync(
            sessionId,
            state =>
            {
                if (recursive)
                {
                    foreach (string ancestor in normalized.Ancestors())
                    {
                        InsertDirectory(state, SessionFsPath.Parse(ancestor), mode);
                    }
                }
                else if (normalized.Parent is { } parent && parent != "/"
                    && (!state.Nodes.TryGetValue(parent, out AzureSessionFsNode? parentNode)
                        || parentNode.Kind != "directory"))
                {
                    throw new DirectoryNotFoundException(
                        $"Parent directory '{parent}' does not exist.");
                }

                InsertDirectory(state, normalized, mode);
            },
            cancellationToken);
    }

    protected override async Task<IList<string>> ReadDirectoryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<KeyValuePair<string, AzureSessionFsNode>> entries =
            await ReadChildrenAsync(path, cancellationToken).ConfigureAwait(false);
        return entries.Select(static entry => SessionFsPath.Parse(entry.Key).Name).ToArray();
    }

    protected override async Task<IList<SessionFsReaddirWithTypesEntry>>
        ReadDirectoryWithTypesAsync(
            string path,
            CancellationToken cancellationToken)
    {
        IReadOnlyList<KeyValuePair<string, AzureSessionFsNode>> entries =
            await ReadChildrenAsync(path, cancellationToken).ConfigureAwait(false);
        return entries
            .Select(entry => new SessionFsReaddirWithTypesEntry
            {
                Name = SessionFsPath.Parse(entry.Key).Name,
                Type = entry.Value.Kind == "directory"
                    ? SessionFsReaddirWithTypesEntryType.Directory
                    : SessionFsReaddirWithTypesEntryType.File,
            })
            .ToArray();
    }

    protected override Task RemoveAsync(
        string path,
        bool recursive,
        bool force,
        CancellationToken cancellationToken)
    {
        SessionFsPath normalized = SessionFsPath.Parse(path);
        if (normalized.Value == "/")
        {
            throw new IOException("The SessionFS root cannot be removed.");
        }

        return store.MutateAsync(
            sessionId,
            state =>
            {
                if (!state.Nodes.TryGetValue(normalized.Value, out AzureSessionFsNode? node))
                {
                    if (force)
                    {
                        return;
                    }

                    throw new FileNotFoundException(
                        $"SessionFS path '{normalized}' does not exist.");
                }

                string[] descendants = state.Nodes.Keys
                    .Where(key => key.StartsWith(normalized.DescendantPrefix, StringComparison.Ordinal))
                    .ToArray();
                if (node.Kind == "directory" && !recursive && descendants.Length != 0)
                {
                    throw new IOException($"SessionFS directory '{normalized}' is not empty.");
                }

                state.Nodes.Remove(normalized.Value);
                if (node.Kind == "directory")
                {
                    foreach (string descendant in descendants)
                    {
                        state.Nodes.Remove(descendant);
                    }
                }
            },
            cancellationToken);
    }

    protected override Task RenameAsync(
        string src,
        string dest,
        CancellationToken cancellationToken)
    {
        SessionFsPath source = SessionFsPath.Parse(src);
        SessionFsPath destination = SessionFsPath.Parse(dest);
        if (source.Value == "/" || destination.Value == "/")
        {
            throw new IOException("The SessionFS root cannot be renamed.");
        }

        return store.MutateAsync(
            sessionId,
            state =>
            {
                if (!state.Nodes.TryGetValue(source.Value, out AzureSessionFsNode? sourceNode))
                {
                    throw new FileNotFoundException(
                        $"SessionFS path '{source}' does not exist.");
                }

                if (sourceNode.Kind == "directory"
                    && destination.Value.StartsWith(
                        source.DescendantPrefix,
                        StringComparison.Ordinal))
                {
                    throw new IOException("A directory cannot be moved into its own subtree.");
                }

                if (state.Nodes.ContainsKey(destination.Value))
                {
                    throw new IOException($"Destination '{destination}' already exists.");
                }

                EnsureAncestors(state, destination, null);
                KeyValuePair<string, AzureSessionFsNode>[] moved = state.Nodes
                    .Where(entry => entry.Key == source.Value
                        || entry.Key.StartsWith(
                            source.DescendantPrefix,
                            StringComparison.Ordinal))
                    .OrderBy(entry => entry.Key.Length)
                    .ToArray();
                foreach (KeyValuePair<string, AzureSessionFsNode> entry in moved)
                {
                    string suffix = entry.Key[source.Value.Length..];
                    state.Nodes[destination.Value + suffix] = entry.Value;
                }

                foreach (KeyValuePair<string, AzureSessionFsNode> entry in moved)
                {
                    state.Nodes.Remove(entry.Key);
                }
            },
            cancellationToken);
    }

    private async Task<IReadOnlyList<KeyValuePair<string, AzureSessionFsNode>>> ReadChildrenAsync(
        string path,
        CancellationToken cancellationToken)
    {
        SessionFsPath normalized = SessionFsPath.Parse(path);
        AzureSessionFsState state =
            await store.ReadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (normalized.Value != "/"
            && (!state.Nodes.TryGetValue(normalized.Value, out AzureSessionFsNode? node)
                || node.Kind != "directory"))
        {
            throw new DirectoryNotFoundException(
                $"SessionFS directory '{normalized}' does not exist.");
        }

        return state.Nodes
            .Where(entry =>
            {
                if (!entry.Key.StartsWith(normalized.DescendantPrefix, StringComparison.Ordinal))
                {
                    return false;
                }

                string remainder = entry.Key[normalized.DescendantPrefix.Length..];
                return remainder.Length != 0
                    && !remainder.Contains('/', StringComparison.Ordinal);
            })
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
    }

    private static SessionFsPath RequireWritablePath(string path)
    {
        SessionFsPath normalized = SessionFsPath.Parse(path);
        return normalized.Value == "/"
            ? throw new IOException("The SessionFS root cannot be written as a file.")
            : normalized;
    }

    private static void EnsureAncestors(
        AzureSessionFsState state,
        SessionFsPath path,
        int? mode)
    {
        foreach (string ancestor in path.Ancestors())
        {
            InsertDirectory(state, SessionFsPath.Parse(ancestor), mode);
        }
    }

    private static void InsertDirectory(
        AzureSessionFsState state,
        SessionFsPath path,
        int? mode)
    {
        if (path.Value == "/")
        {
            return;
        }

        if (state.Nodes.TryGetValue(path.Value, out AzureSessionFsNode? existing))
        {
            if (existing.Kind == "file")
            {
                throw new IOException($"SessionFS path '{path}' is a file.");
            }

            return;
        }

        state.Nodes[path.Value] = NewNode(
            "directory",
            content: null,
            mode,
            DateTimeOffset.UtcNow);
    }

    private static AzureSessionFsNode NewNode(
        string kind,
        string? content,
        int? mode,
        DateTimeOffset now) =>
        new()
        {
            Kind = kind,
            Content = content,
            Mode = mode,
            Birthtime = now,
            Mtime = now,
            Version = 0,
        };
}
