namespace CopilotSessionPersistencePoc.Execution;

public interface IExecutionJobRepository
{
    Task<ExecutionJob?> GetAsync(
        string toolCallId,
        CancellationToken cancellationToken);

    Task<ExecutionJob?> GetByJobIdAsync(
        string sessionId,
        string jobId,
        CancellationToken cancellationToken);

    Task<ExecutionJobReservation> GetOrCreateAsync(
        string sessionId,
        string toolCallId,
        string codeSha256,
        CancellationToken cancellationToken);

    Task<ExecutionJob> UpdateAsync(
        ExecutionJob job,
        ExecutionJobStatus status,
        string? standardOutput,
        string? standardError,
        string? failureMessage,
        string? outputsJson,
        CancellationToken cancellationToken);

    Task DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken);
}

public sealed class ExecutionJobConcurrencyException(string message, Exception innerException)
    : IOException(message, innerException);

public sealed record ExecutionJob(
    string StorageKey,
    string JobId,
    string SessionId,
    string ToolCallId,
    string CodeSha256,
    ExecutionJobStatus Status,
    string? StandardOutput,
    string? StandardError,
    string? Error,
    string? OutputsJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Azure.ETag ETag);

public sealed record ExecutionJobReservation(ExecutionJob Job, bool Created);

public enum ExecutionJobStatus
{
    Pending,
    Preparing,
    Running,
    Publishing,
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,
}

public static class ExecutionJobStatusExtensions
{
    public static bool IsTerminal(this ExecutionJobStatus status) =>
        status is ExecutionJobStatus.Succeeded
            or ExecutionJobStatus.Failed
            or ExecutionJobStatus.TimedOut
            or ExecutionJobStatus.Cancelled;
}
