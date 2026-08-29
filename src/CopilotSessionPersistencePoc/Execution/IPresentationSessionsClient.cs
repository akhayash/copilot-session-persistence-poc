namespace CopilotSessionPersistencePoc.Execution;

public interface IPresentationSessionsClient
{
    Task<PresentationWorkerManifest> CreatePresentationAsync(
        string identifier,
        PresentationWorkerRequest request,
        CancellationToken cancellationToken);

    Task<BinaryData> DownloadArtifactAsync(
        string identifier,
        string fileName,
        CancellationToken cancellationToken);

    Task StopSessionAsync(
        string identifier,
        CancellationToken cancellationToken);
}

public sealed record PresentationWorkerRequest(
    string FileName,
    string Title,
    string? Subtitle,
    string Audience,
    IReadOnlyList<PresentationSlide> Slides);

public sealed record PresentationSlide(
    string Title,
    string Body,
    string? Highlight);

public sealed record PresentationWorkerManifest(
    bool ValidationPassed,
    int SlideCount,
    IReadOnlyList<PresentationWorkerFile> Files);

public sealed record PresentationWorkerFile(
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256);

public sealed class PresentationSessionsException(
    string message,
    Exception? innerException = null)
    : IOException(message, innerException);
