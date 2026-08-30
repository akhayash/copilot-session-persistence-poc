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

    Task<PresentationExecResult> ExecuteAsync(
        string identifier,
        string command,
        int timeoutSeconds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PresentationWorkspaceFile>> ListFilesAsync(
        string identifier,
        CancellationToken cancellationToken);

    Task<PresentationWorkspaceFile> WriteFileAsync(
        string identifier,
        string path,
        BinaryData content,
        CancellationToken cancellationToken);

    Task<BinaryData> ReadFileAsync(
        string identifier,
        string path,
        CancellationToken cancellationToken);

    Task DeleteFileAsync(
        string identifier,
        string path,
        CancellationToken cancellationToken);

    Task<PresentationRenderResult> RenderAsync(
        string identifier,
        string path,
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

public sealed record PresentationExecResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated);

public sealed record PresentationWorkspaceFile(
    string Path,
    long SizeBytes,
    string Sha256);

public sealed record PresentationRenderImage(
    int SlideNumber,
    string MimeType,
    BinaryData Content);

public sealed record PresentationRenderResult(
    bool ValidationPassed,
    int SlideCount,
    IReadOnlyList<PresentationRenderImage> Images);

public sealed class PresentationSessionsException(
    string message,
    Exception? innerException = null)
    : IOException(message, innerException);
