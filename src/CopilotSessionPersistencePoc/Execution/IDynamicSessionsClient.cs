namespace CopilotSessionPersistencePoc.Execution;

public interface IDynamicSessionsClient
{
    Task UploadFileAsync(
        string identifier,
        string fileName,
        string contentType,
        BinaryData content,
        CancellationToken cancellationToken);

    Task<DynamicSessionExecutionResult> ExecuteCodeAsync(
        string identifier,
        string code,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DynamicSessionFile>> ListFilesAsync(
        string identifier,
        CancellationToken cancellationToken);

    Task<BinaryData> DownloadFileAsync(
        string identifier,
        string fileName,
        CancellationToken cancellationToken);

    Task DeleteSessionAsync(
        string identifier,
        CancellationToken cancellationToken);
}

public sealed record DynamicSessionExecutionResult(
    string Status,
    string StandardOutput,
    string StandardError,
    string ExecutionResult,
    long ExecutionTimeInMilliseconds);

public sealed record DynamicSessionFile(
    string FileName,
    long Size,
    DateTimeOffset? LastModifiedTime);

public sealed class DynamicSessionsException(string message, Exception? innerException = null)
    : IOException(message, innerException);
