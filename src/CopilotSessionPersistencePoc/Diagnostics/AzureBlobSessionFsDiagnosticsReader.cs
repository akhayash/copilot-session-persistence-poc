using System.Text;
using CopilotSessionPersistencePoc.Persistence;
using CopilotSessionPersistencePoc.SessionFs;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Diagnostics;

public sealed class AzureBlobSessionFsDiagnosticsReader(
    AzureBlobSessionFsStore store,
    IOptions<DiagnosticsOptions> options,
    IOptions<AzureStorageOptions> storageOptions)
    : ISessionFsDiagnosticsReader
{
    public async Task<SessionFsDiagnosticsSnapshot> GetSnapshotAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        AzureSessionFsState state =
            await store.ReadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        SessionFsEntryInfo[] entries = state.Nodes
            .Select(static entry => ToEntry(entry.Key, entry.Value))
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        string? events = state.Nodes.TryGetValue(
            "/session-state/events.jsonl",
            out AzureSessionFsNode? eventNode)
            && eventNode.Kind == "file"
                ? eventNode.Content
                : null;
        long? blobSize =
            await store.GetStateSizeAsync(sessionId, cancellationToken).ConfigureAwait(false);
        string hostSessionDirectory = GetHostSessionDirectory(sessionId);
        bool hostDirectoryExists = Directory.Exists(hostSessionDirectory);
        bool individualFilesDetected = hostDirectoryExists
            && Directory.EnumerateFiles(hostSessionDirectory, "*", SearchOption.AllDirectories).Any();

        return new SessionFsDiagnosticsSnapshot(
            sessionId,
            entries.Length,
            entries.Count(static entry => entry.Kind == "file"),
            entries.Count(static entry => entry.Kind == "directory"),
            entries.Sum(static entry => entry.SizeBytes),
            events?.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length ?? 0,
            entries.Length == 0 ? null : entries.Max(static entry => entry.ModifiedTime),
            new SessionFsStorageEvidence(
                "AzureStorage",
                $"Azure Table Storage: {storageOptions.Value.AppSessionsTable}",
                $"Azure Blob Storage: {storageOptions.Value.SessionFsContainer}",
                $"Azure Blob lease: {storageOptions.Value.SessionLocksContainer}",
                $"Azure Blob Storage: {storageOptions.Value.ArtifactsContainer}",
                "Azure Blob Storage custom SessionFS provider",
                store.GetStateUri(sessionId).ToString(),
                blobSize.HasValue,
                blobSize ?? 0,
                hostSessionDirectory,
                hostDirectoryExists,
                individualFilesDetected,
                individualFilesDetected
                    ? "Individual files were found under the matching host session directory; inspect them before claiming Blob-only storage."
                    : "SessionFS nodes are stored in a shared Azure Blob snapshot, not as individual host files."),
            entries);
    }

    public async Task<SessionFsEntryDetails?> GetEntryAsync(
        string sessionId,
        string path,
        CancellationToken cancellationToken = default)
    {
        SessionFsPath normalized = SessionFsPath.Parse(path);
        AzureSessionFsState state =
            await store.ReadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!state.Nodes.TryGetValue(normalized.Value, out AzureSessionFsNode? node))
        {
            return null;
        }

        int maximumCharacters = options.Value.MaximumPreviewCharacters;
        string? content = node.Content;
        int originalCharacterCount = content?.Length ?? 0;
        bool truncated = originalCharacterCount > maximumCharacters;
        if (truncated)
        {
            content = content![..maximumCharacters];
        }

        return new SessionFsEntryDetails(
            ToEntry(normalized.Value, node),
            content is null ? null : DiagnosticsContentRedactor.Redact(content, truncated),
            truncated,
            originalCharacterCount,
            "state.json nodes",
            sessionId,
            normalized.Value);
    }

    private static SessionFsEntryInfo ToEntry(string path, AzureSessionFsNode node) =>
        new(
            path,
            ClassifyPath(path),
            node.Kind,
            node.Kind == "file"
                ? Encoding.UTF8.GetByteCount(node.Content ?? string.Empty)
                : 0,
            node.Birthtime,
            node.Mtime,
            node.Version);

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

    private static string GetHostSessionDirectory(string sessionId) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot",
            "session-state",
            sessionId);
}
