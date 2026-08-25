namespace CopilotSessionPersistencePoc.Diagnostics;

public interface ISessionFsDiagnosticsReader
{
    Task<SessionFsDiagnosticsSnapshot> GetSnapshotAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<SessionFsEntryDetails?> GetEntryAsync(
        string sessionId,
        string path,
        CancellationToken cancellationToken = default);
}
