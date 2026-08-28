namespace CopilotSessionPersistencePoc.ArtifactStorage;

public sealed class UnavailableArtifactStore : IArtifactStore
{
    public Task<IReadOnlyList<ArtifactInfo>> ListAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        throw new ArtifactStorageUnavailableException();

    public Task<ArtifactInfo> PutAsync(
        string sessionId,
        string artifactId,
        string fileName,
        string contentType,
        BinaryData content,
        CancellationToken cancellationToken = default) =>
        throw new ArtifactStorageUnavailableException();

    public Task<ArtifactContent?> GetAsync(
        string sessionId,
        string artifactId,
        string fileName,
        CancellationToken cancellationToken = default) =>
        throw new ArtifactStorageUnavailableException();

    public Task DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public sealed class ArtifactStorageUnavailableException()
    : InvalidOperationException(
        "Artifact storage is available only when Persistence:Backend is AzureStorage.");
