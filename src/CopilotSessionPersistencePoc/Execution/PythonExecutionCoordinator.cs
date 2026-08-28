using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using CopilotSessionPersistencePoc.AppState;
using CopilotSessionPersistencePoc.ArtifactStorage;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Execution;

public sealed class PythonExecutionCoordinator(
    IAppSessionRepository sessions,
    IArtifactStore artifacts,
    IExecutionJobRepository jobs,
    IDynamicSessionsClient dynamicSessions,
    IOptions<DynamicSessionsOptions> options,
    ILogger<PythonExecutionCoordinator> logger)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly Action<ILogger, string, Exception?> LogCleanupFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogCleanupFailure)),
            "Dynamic session cleanup failed for execution job {JobId}.");
    private readonly DynamicSessionsOptions settings = options.Value;

    public async Task<IReadOnlyList<ExecutionArtifactReference>> ListArtifactsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        await EnsureSessionExistsAsync(sessionId, cancellationToken);
        IReadOnlyList<ArtifactInfo> items =
            await artifacts.ListAsync(sessionId, cancellationToken);
        var published = new List<ArtifactInfo>(items.Count);
        foreach (IGrouping<string, ArtifactInfo> group in items.GroupBy(
            static item => item.ArtifactId,
            StringComparer.Ordinal))
        {
            if (!IsGeneratedArtifact(group.Key)
                || await IsSucceededAsync(
                    sessionId,
                    group.Key,
                    cancellationToken))
            {
                published.AddRange(group);
            }
        }

        return published
            .Select(static item => ExecutionArtifactReference.From(item))
            .ToArray();
    }

    public async Task<PythonExecutionResult> ExecuteAsync(
        string sessionId,
        string toolCallId,
        string code,
        IReadOnlyList<string>? inputFiles,
        CancellationToken cancellationToken)
    {
        await EnsureSessionExistsAsync(sessionId, cancellationToken);
        ValidateCode(code);
        string[] requestedInputs = inputFiles?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        if (requestedInputs.Length > settings.MaximumInputFiles)
        {
            throw new ArgumentException(
                $"A maximum of {settings.MaximumInputFiles} input files is allowed.",
                nameof(inputFiles));
        }

        string codeSha256 = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        ExecutionJobReservation reservation = await jobs.GetOrCreateAsync(
            sessionId,
            toolCallId,
            codeSha256,
            cancellationToken);
        if (!reservation.Created)
        {
            return await WaitForExistingAsync(
                reservation.Job,
                cancellationToken);
        }

        ExecutionJob job = reservation.Job;
        using var ownerCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ownerCancellation.CancelAfter(
            settings.StaleJobTimeout - TimeSpan.FromSeconds(30));
        CancellationToken operationToken = ownerCancellation.Token;
        string sandboxIdentifier = Convert.ToHexStringLower(
            RandomNumberGenerator.GetBytes(32));
        bool sandboxAllocated = false;
        try
        {
            job = await jobs.UpdateAsync(
                job,
                ExecutionJobStatus.Preparing,
                null,
                null,
                null,
                null,
                operationToken);
            Dictionary<string, ArtifactContent> inputs = await LoadInputsAsync(
                sessionId,
                requestedInputs,
                operationToken);
            foreach ((string _, ArtifactContent input) in inputs)
            {
                sandboxAllocated = true;
                await dynamicSessions.UploadFileAsync(
                    sandboxIdentifier,
                    input.Info.FileName,
                    input.Info.ContentType,
                    input.Content,
                    operationToken);
            }

            job = await jobs.UpdateAsync(
                job,
                ExecutionJobStatus.Running,
                null,
                null,
                null,
                null,
                operationToken);
            sandboxAllocated = true;
            DynamicSessionExecutionResult execution =
                await dynamicSessions.ExecuteCodeAsync(
                    sandboxIdentifier,
                    code,
                    operationToken);
            string stdout = Bound(execution.StandardOutput);
            string stderr = Bound(execution.StandardError);
            if (!execution.Status.Equals("Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                ExecutionJobStatus executionStatus =
                    execution.Status.Equals("TimedOut", StringComparison.OrdinalIgnoreCase)
                    || execution.Status.Equals("Timeout", StringComparison.OrdinalIgnoreCase)
                        ? ExecutionJobStatus.TimedOut
                        : execution.Status.Equals("Canceled", StringComparison.OrdinalIgnoreCase)
                            || execution.Status.Equals(
                                "Cancelled",
                                StringComparison.OrdinalIgnoreCase)
                            ? ExecutionJobStatus.Cancelled
                            : ExecutionJobStatus.Failed;
                job = await jobs.UpdateAsync(
                    job,
                    executionStatus,
                    stdout,
                    stderr,
                    $"Dynamic Sessions reported status '{execution.Status}'.",
                    null,
                    CancellationToken.None);
                return ToResult(job);
            }

            job = await jobs.UpdateAsync(
                job,
                ExecutionJobStatus.Publishing,
                stdout,
                stderr,
                null,
                null,
                operationToken);
            IReadOnlyList<ArtifactInfo> outputs = await PublishOutputsAsync(
                sessionId,
                job.JobId,
                sandboxIdentifier,
                inputs,
                operationToken);
            string outputsJson = JsonSerializer.Serialize(
                outputs.Select(static output => ExecutionArtifactReference.From(output)),
                JsonOptions);
            job = await jobs.UpdateAsync(
                job,
                ExecutionJobStatus.Succeeded,
                stdout,
                stderr,
                null,
                outputsJson,
                operationToken);
            return ToResult(job);
        }
        catch (Exception exception)
            when (exception is DynamicSessionsException
                or HttpRequestException
                or IOException
                or RequestFailedException
                or OperationCanceledException
                or ArgumentException)
        {
            ExecutionJobStatus status =
                exception is OperationCanceledException
                    ? cancellationToken.IsCancellationRequested
                        ? ExecutionJobStatus.Cancelled
                        : ExecutionJobStatus.TimedOut
                    : ExecutionJobStatus.Failed;
            try
            {
                await jobs.UpdateAsync(
                    job,
                    status,
                    job.StandardOutput,
                    job.StandardError,
                    Bound(exception.Message),
                    job.OutputsJson,
                    CancellationToken.None);
            }
            catch (Exception updateException)
                when (updateException is RequestFailedException or IOException)
            {
                throw new AggregateException(
                    "Python execution and job-state persistence both failed.",
                    exception,
                    updateException);
            }

            throw;
        }
        finally
        {
            if (sandboxAllocated)
            {
                try
                {
                    await dynamicSessions.DeleteSessionAsync(
                        sandboxIdentifier,
                        CancellationToken.None);
                }
                catch (DynamicSessionsException cleanupException)
                {
                    LogCleanupFailure(logger, job.JobId, cleanupException);
                }
            }
        }
    }

    private async Task<PythonExecutionResult> WaitForExistingAsync(
        ExecutionJob job,
        CancellationToken cancellationToken)
    {
        while (!job.Status.IsTerminal())
        {
            if (DateTimeOffset.UtcNow - job.UpdatedAt >= settings.StaleJobTimeout)
            {
                try
                {
                    job = await jobs.UpdateAsync(
                        job,
                        ExecutionJobStatus.Failed,
                        job.StandardOutput,
                        job.StandardError,
                        "The execution owner stopped before the job reached a terminal state.",
                        job.OutputsJson,
                        cancellationToken);
                    break;
                }
                catch (ExecutionJobConcurrencyException)
                {
                    job = await jobs.GetAsync(job.ToolCallId, cancellationToken)
                        ?? throw new IOException(
                            $"Execution job '{job.JobId}' disappeared while waiting.");
                    continue;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            job = await jobs.GetAsync(job.ToolCallId, cancellationToken)
                ?? throw new IOException(
                    $"Execution job '{job.JobId}' disappeared while waiting.");
        }

        return ToResult(job);
    }

    private async Task<Dictionary<string, ArtifactContent>> LoadInputsAsync(
        string sessionId,
        IReadOnlyList<string> requestedInputs,
        CancellationToken cancellationToken)
    {
        var inputs = new Dictionary<string, ArtifactContent>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (string reference in requestedInputs)
        {
            (string artifactId, string fileName) = ParseReference(reference);
            ArtifactContent? content = await artifacts.GetAsync(
                sessionId,
                artifactId,
                fileName,
                cancellationToken);
            if (content is null)
            {
                throw new FileNotFoundException(
                    $"Input artifact '{reference}' was not found.");
            }

            totalBytes += content.Info.SizeBytes;
            if (totalBytes > settings.MaximumInputBytes)
            {
                throw new ArgumentException(
                    $"Input files exceed the {settings.MaximumInputBytes} byte limit.",
                    nameof(requestedInputs));
            }

            if (!inputs.TryAdd(fileName, content))
            {
                throw new ArgumentException(
                    $"Input file name '{fileName}' is duplicated.",
                    nameof(requestedInputs));
            }
        }

        return inputs;
    }

    private async Task<IReadOnlyList<ArtifactInfo>> PublishOutputsAsync(
        string sessionId,
        string jobId,
        string sandboxIdentifier,
        Dictionary<string, ArtifactContent> inputs,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DynamicSessionFile> files =
            await dynamicSessions.ListFilesAsync(sandboxIdentifier, cancellationToken);
        if (files.Count > settings.MaximumInputFiles + settings.MaximumOutputFiles)
        {
            throw new IOException(
                "The sandbox produced more files than the configured limit.");
        }

        var outputs = new List<ArtifactInfo>();
        long totalBytes = 0;
        foreach (DynamicSessionFile file in files)
        {
            ValidateSandboxFileName(file.FileName);
            if (file.Size > settings.MaximumOutputBytes)
            {
                throw new IOException(
                    $"Sandbox file '{file.FileName}' exceeds the output size limit.");
            }

            BinaryData content = await dynamicSessions.DownloadFileAsync(
                sandboxIdentifier,
                file.FileName,
                cancellationToken);
            string hash = Convert.ToHexStringLower(
                SHA256.HashData(content.ToMemory().Span));
            if (inputs.TryGetValue(file.FileName, out ArtifactContent? input)
                && hash.Equals(input.Info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (outputs.Count >= settings.MaximumOutputFiles)
            {
                throw new IOException(
                    "The sandbox produced more output files than the configured limit.");
            }

            totalBytes += content.ToMemory().Length;
            if (totalBytes > settings.MaximumOutputBytes)
            {
                throw new IOException(
                    "Sandbox outputs exceed the configured total size limit.");
            }

            outputs.Add(await artifacts.PutAsync(
                sessionId,
                jobId,
                file.FileName,
                GetContentType(file.FileName),
                content,
                cancellationToken));
        }

        return outputs;
    }

    private async Task EnsureSessionExistsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (await sessions.GetAsync(sessionId, cancellationToken) is null)
        {
            throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
        }
    }

    private async Task<bool> IsSucceededAsync(
        string sessionId,
        string artifactId,
        CancellationToken cancellationToken) =>
        (await jobs.GetByJobIdAsync(sessionId, artifactId, cancellationToken))?.Status
            == ExecutionJobStatus.Succeeded;

    private static bool IsGeneratedArtifact(string artifactId) =>
        artifactId.StartsWith("job-", StringComparison.Ordinal);

    private void ValidateCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (code.Length > settings.MaximumCodeCharacters)
        {
            throw new ArgumentException(
                $"Python code exceeds {settings.MaximumCodeCharacters} characters.",
                nameof(code));
        }
    }

    private static (string ArtifactId, string FileName) ParseReference(string reference)
    {
        string[] parts = reference.Split('/', 2);
        if (parts.Length != 2
            || string.IsNullOrWhiteSpace(parts[0])
            || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new ArgumentException(
                $"Artifact reference '{reference}' must use 'artifactId/fileName'.",
                nameof(reference));
        }

        ValidateSandboxFileName(parts[1]);
        return (parts[0], parts[1]);
    }

    private static void ValidateSandboxFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName is "." or ".."
            || fileName.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            throw new IOException(
                $"Sandbox returned unsafe file name '{fileName}'.");
        }
    }

    private string Bound(string value) =>
        value.Length <= settings.MaximumCapturedCharacters
            ? value
            : value[..settings.MaximumCapturedCharacters];

    private static string GetContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".csv" => "text/csv",
            ".json" => "application/json",
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".pptx" =>
                "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => "application/octet-stream",
        };

    private static PythonExecutionResult ToResult(ExecutionJob job)
    {
        ExecutionArtifactReference[] outputs =
            string.IsNullOrWhiteSpace(job.OutputsJson)
                ? []
                : JsonSerializer.Deserialize<ExecutionArtifactReference[]>(
                    job.OutputsJson,
                    JsonOptions) ?? [];
        return new PythonExecutionResult(
            job.JobId,
            job.Status.ToString(),
            job.StandardOutput,
            job.StandardError,
            job.Error,
            outputs);
    }
}

public sealed record PythonExecutionResult(
    string JobId,
    string Status,
    string? StandardOutput,
    string? StandardError,
    string? Error,
    IReadOnlyList<ExecutionArtifactReference> Outputs);

public sealed record ExecutionArtifactReference(
    string ArtifactId,
    string FileName,
    string ContentType,
    string Sha256,
    long SizeBytes)
{
    public static ExecutionArtifactReference From(ArtifactInfo artifact) => new(
        artifact.ArtifactId,
        artifact.FileName,
        artifact.ContentType,
        artifact.Sha256,
        artifact.SizeBytes);
}
