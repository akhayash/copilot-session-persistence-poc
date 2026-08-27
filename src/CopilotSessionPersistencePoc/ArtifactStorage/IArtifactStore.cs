namespace CopilotSessionPersistencePoc.ArtifactStorage;

public interface IArtifactStore
{
    Task<ArtifactInfo> PutAsync(
        string sessionId,
        string artifactId,
        string fileName,
        string contentType,
        BinaryData content,
        CancellationToken cancellationToken = default);

    Task<ArtifactContent?> GetAsync(
        string sessionId,
        string artifactId,
        string fileName,
        CancellationToken cancellationToken = default);

    Task DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}

public sealed record ArtifactInfo(
    string SessionId,
    string ArtifactId,
    string FileName,
    string ContentType,
    string Sha256,
    long SizeBytes,
    Uri StorageUri);

public sealed record ArtifactContent(ArtifactInfo Info, BinaryData Content);
