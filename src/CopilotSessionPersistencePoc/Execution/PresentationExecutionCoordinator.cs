using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Xml.Linq;
using Azure;
using CopilotSessionPersistencePoc.AppState;
using CopilotSessionPersistencePoc.ArtifactStorage;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Execution;

public sealed class PresentationExecutionCoordinator(
    IAppSessionRepository sessions,
    IArtifactStore artifacts,
    IExecutionJobRepository jobs,
    IPresentationSessionsClient presentationSessions,
    IOptions<PresentationSessionsOptions> options,
    ILogger<PresentationExecutionCoordinator> logger)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly Action<ILogger, string, Exception?> LogCleanupFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogCleanupFailure)),
            "Presentation session cleanup failed for execution job {JobId}.");
    private static readonly Action<ILogger, string, Exception?> LogRollbackFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2, nameof(LogRollbackFailure)),
            "Presentation artifact rollback failed for execution job {JobId}.");
    private readonly PresentationSessionsOptions settings = options.Value;

    public async Task<PresentationExecutionResult> ExecuteAsync(
        string sessionId,
        string toolCallId,
        PresentationWorkerRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureSessionExistsAsync(sessionId, cancellationToken);
        ValidateRequest(request);
        string requestJson = JsonSerializer.Serialize(request, JsonOptions);
        string requestSha256 = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(requestJson)));
        ExecutionJobReservation reservation = await jobs.GetOrCreateAsync(
            sessionId,
            toolCallId,
            requestSha256,
            cancellationToken);
        if (!reservation.Created)
        {
            return await WaitForExistingAsync(
                reservation.Job,
                cancellationToken);
        }

        ExecutionJob job = reservation.Job;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
        string identifier = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        bool sessionAllocated = false;
        bool artifactsPublished = false;
        try
        {
            job = await jobs.UpdateAsync(
                job,
                ExecutionJobStatus.Preparing,
                null,
                null,
                null,
                null,
                timeout.Token);
            job = await jobs.UpdateAsync(
                job,
                ExecutionJobStatus.Running,
                null,
                null,
                null,
                null,
                timeout.Token);
            sessionAllocated = true;
            PresentationWorkerManifest manifest =
                await presentationSessions.CreatePresentationAsync(
                    identifier,
                    request,
                    timeout.Token);
            ValidateManifest(request, manifest);
            job = await jobs.UpdateAsync(
                job,
                ExecutionJobStatus.Publishing,
                $"Validated {manifest.SlideCount} slides.",
                string.Empty,
                null,
                null,
                timeout.Token);
            IReadOnlyList<ArtifactInfo> outputs = await PublishAsync(
                sessionId,
                job.JobId,
                identifier,
                manifest,
                timeout.Token);
            artifactsPublished = true;
            string outputsJson = JsonSerializer.Serialize(
                outputs.Select(static output => ExecutionArtifactReference.From(output)),
                JsonOptions);
            job = await jobs.UpdateAsync(
                job,
                ExecutionJobStatus.Succeeded,
                job.StandardOutput,
                string.Empty,
                null,
                outputsJson,
                timeout.Token);
            artifactsPublished = false;
            return ToResult(job);
        }
        catch (Exception exception)
            when (exception is PresentationSessionsException
                or HttpRequestException
                or IOException
                or RequestFailedException
                or OperationCanceledException
                or ArgumentException)
        {
            if (artifactsPublished)
            {
                using var rollbackTimeout =
                    new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await artifacts.DeleteAsync(
                        sessionId,
                        job.JobId,
                        rollbackTimeout.Token);
                }
                catch (Exception rollbackException)
                    when (rollbackException is RequestFailedException
                        or IOException
                        or OperationCanceledException)
                {
                    LogRollbackFailure(logger, job.JobId, rollbackException);
                }
            }

            ExecutionJobStatus status =
                exception is OperationCanceledException
                    ? cancellationToken.IsCancellationRequested
                        ? ExecutionJobStatus.Cancelled
                        : ExecutionJobStatus.TimedOut
                    : ExecutionJobStatus.Failed;
            await jobs.UpdateAsync(
                job,
                status,
                job.StandardOutput,
                job.StandardError,
                Bound(exception.Message),
                job.OutputsJson,
                CancellationToken.None);
            throw;
        }
        finally
        {
            if (sessionAllocated)
            {
                using var cleanupTimeout =
                    new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    await presentationSessions.StopSessionAsync(
                        identifier,
                        cleanupTimeout.Token);
                }
                catch (Exception exception)
                {
                    LogCleanupFailure(logger, job.JobId, exception);
                }
            }
        }
    }

    private async Task<PresentationExecutionResult> WaitForExistingAsync(
        ExecutionJob job,
        CancellationToken cancellationToken)
    {
        TimeSpan staleAfter =
            TimeSpan.FromSeconds(settings.RequestTimeoutSeconds + 60);
        while (!job.Status.IsTerminal())
        {
            if (DateTimeOffset.UtcNow - job.UpdatedAt >= staleAfter)
            {
                try
                {
                    job = await jobs.UpdateAsync(
                        job,
                        ExecutionJobStatus.Failed,
                        job.StandardOutput,
                        job.StandardError,
                        "The presentation execution owner stopped before completion.",
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

    private async Task<IReadOnlyList<ArtifactInfo>> PublishAsync(
        string sessionId,
        string jobId,
        string identifier,
        PresentationWorkerManifest manifest,
        CancellationToken cancellationToken)
    {
        var validated = new List<(PresentationWorkerFile File, BinaryData Content)>(
            manifest.Files.Count);
        long totalBytes = 0;
        foreach (PresentationWorkerFile file in manifest.Files)
        {
            BinaryData content = await presentationSessions.DownloadArtifactAsync(
                identifier,
                file.FileName,
                cancellationToken);
            if (content.ToMemory().Length != file.SizeBytes)
            {
                throw new IOException(
                    $"Presentation artifact '{file.FileName}' size did not match its manifest.");
            }

            string hash = Convert.ToHexStringLower(
                SHA256.HashData(content.ToMemory().Span));
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"Presentation artifact '{file.FileName}' hash did not match its manifest.");
            }

            totalBytes += file.SizeBytes;
            if (totalBytes > settings.MaximumOutputBytes)
            {
                throw new IOException(
                    "Presentation artifacts exceed the configured output size limit.");
            }

            ValidateContent(file, content, manifest);
            validated.Add((file, content));
        }

        var outputs = new List<ArtifactInfo>(validated.Count);
        try
        {
            foreach ((PresentationWorkerFile file, BinaryData content) in validated)
            {
                outputs.Add(await artifacts.PutAsync(
                    sessionId,
                    jobId,
                    file.FileName,
                    file.ContentType,
                    content,
                    cancellationToken));
            }

            return outputs;
        }
        catch
        {
            using var rollbackTimeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await artifacts.DeleteAsync(
                    sessionId,
                    jobId,
                    rollbackTimeout.Token);
            }
            catch (Exception exception)
                when (exception is RequestFailedException
                    or IOException
                    or OperationCanceledException)
            {
                LogRollbackFailure(logger, jobId, exception);
            }

            throw;
        }
    }

    private static void ValidateContent(
        PresentationWorkerFile file,
        BinaryData content,
        PresentationWorkerManifest manifest)
    {
        ReadOnlyMemory<byte> bytes = content.ToMemory();
        switch (Path.GetExtension(file.FileName).ToLowerInvariant())
        {
            case ".pptx":
                try
                {
                    using var stream = new MemoryStream(bytes.ToArray(), writable: false);
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                    ZipArchiveEntry? contentTypes = archive.GetEntry("[Content_Types].xml");
                    ZipArchiveEntry? presentation = archive.GetEntry("ppt/presentation.xml");
                    ZipArchiveEntry? relationships =
                        archive.GetEntry("ppt/_rels/presentation.xml.rels");
                    ZipArchiveEntry[] slides = archive.Entries
                        .Where(entry =>
                            entry.FullName.StartsWith(
                                "ppt/slides/slide",
                                StringComparison.Ordinal)
                            && entry.FullName.EndsWith(
                                ".xml",
                                StringComparison.Ordinal))
                        .ToArray();
                    if (contentTypes is null
                        || presentation is null
                        || relationships is null
                        || slides.Length != manifest.SlideCount
                        || slides.Any(static entry => entry.Length == 0))
                    {
                        throw new IOException(
                            $"Presentation artifact '{file.FileName}' is not a valid PPTX package.");
                    }

                    XNamespace presentationNamespace =
                        "http://schemas.openxmlformats.org/presentationml/2006/main";
                    XDocument presentationXml = LoadXml(presentation);
                    int declaredSlides = presentationXml
                        .Descendants(presentationNamespace + "sldId")
                        .Count();
                    XDocument relationshipsXml = LoadXml(relationships);
                    XNamespace relationshipsNamespace =
                        "http://schemas.openxmlformats.org/package/2006/relationships";
                    int slideRelationships = relationshipsXml
                        .Descendants(relationshipsNamespace + "Relationship")
                        .Count(element =>
                            ((string?)element.Attribute("Type"))?.EndsWith(
                                "/slide",
                                StringComparison.Ordinal) == true);
                    if (declaredSlides != manifest.SlideCount
                        || slideRelationships != manifest.SlideCount)
                    {
                        throw new IOException(
                            $"Presentation artifact '{file.FileName}' has inconsistent slide relationships.");
                    }

                    _ = LoadXml(contentTypes);
                    foreach (ZipArchiveEntry slide in slides)
                    {
                        _ = LoadXml(slide);
                    }
                }
                catch (Exception exception)
                    when (exception is InvalidDataException
                        or System.Xml.XmlException)
                {
                    throw new IOException(
                        $"Presentation artifact '{file.FileName}' is not a valid PPTX package.",
                        exception);
                }

                break;
            case ".pdf":
                if (bytes.Length < 12
                    || !bytes.Span[..5].SequenceEqual("%PDF-"u8)
                    || Encoding.Latin1.GetString(bytes.Span)
                        .TrimEnd()
                        .EndsWith("%%EOF", StringComparison.Ordinal) is false)
                {
                    throw new IOException(
                        $"Presentation artifact '{file.FileName}' is not a valid PDF.");
                }

                string pdfText = Encoding.Latin1.GetString(bytes.Span);
                int pageCount = System.Text.RegularExpressions.Regex.Count(
                    pdfText,
                    @"/Type\s*/Page(?!s)\b",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                if (pageCount != manifest.SlideCount)
                {
                    throw new IOException(
                        $"Presentation artifact '{file.FileName}' has an invalid page count.");
                }

                break;
            case ".png":
                ReadOnlySpan<byte> pngSignature =
                    [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
                if (bytes.Length < 33
                    || !bytes.Span[..pngSignature.Length].SequenceEqual(pngSignature))
                {
                    throw new IOException(
                        $"Presentation artifact '{file.FileName}' is not a valid PNG.");
                }

                ReadOnlySpan<byte> png = bytes.Span;
                int ihdrLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(
                    png.Slice(8, 4));
                int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(
                    png.Slice(16, 4));
                int height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(
                    png.Slice(20, 4));
                if (ihdrLength != 13
                    || !png.Slice(12, 4).SequenceEqual("IHDR"u8)
                    || width <= 0
                    || height <= 0
                    || !png[^8..^4].SequenceEqual("IEND"u8))
                {
                    throw new IOException(
                        $"Presentation artifact '{file.FileName}' has an invalid PNG structure.");
                }

                break;
            case ".json":
                try
                {
                    using JsonDocument document = JsonDocument.Parse(bytes);
                    JsonElement root = document.RootElement;
                    if (root.ValueKind is not JsonValueKind.Object
                        || !root.TryGetProperty("validationPassed", out JsonElement passed)
                        || passed.ValueKind is not JsonValueKind.True
                        || !root.TryGetProperty("slideCount", out JsonElement slideCount)
                        || slideCount.GetInt32() != manifest.SlideCount
                        || !root.TryGetProperty("files", out JsonElement files)
                        || files.ValueKind is not JsonValueKind.Array)
                    {
                        throw new IOException(
                            $"Presentation artifact '{file.FileName}' has an invalid validation schema.");
                    }

                    PresentationWorkerFile[] expected = manifest.Files
                        .Where(static candidate =>
                            !Path.GetExtension(candidate.FileName).Equals(
                                ".json",
                                StringComparison.OrdinalIgnoreCase))
                        .OrderBy(static candidate => candidate.FileName, StringComparer.Ordinal)
                        .ToArray();
                    PresentationWorkerFile[] audited = files
                        .EnumerateArray()
                        .Select(static item => new PresentationWorkerFile(
                            item.GetProperty("fileName").GetString() ?? string.Empty,
                            item.GetProperty("contentType").GetString() ?? string.Empty,
                            item.GetProperty("sizeBytes").GetInt64(),
                            item.GetProperty("sha256").GetString() ?? string.Empty))
                        .OrderBy(static candidate => candidate.FileName, StringComparer.Ordinal)
                        .ToArray();
                    if (!audited.SequenceEqual(expected))
                    {
                        throw new IOException(
                            $"Presentation artifact '{file.FileName}' does not match the returned manifest.");
                    }
                }
                catch (Exception exception)
                    when (exception is JsonException
                        or InvalidOperationException
                        or KeyNotFoundException
                        or FormatException)
                {
                    throw new IOException(
                        $"Presentation artifact '{file.FileName}' is not valid JSON.",
                        exception);
                }

                break;
        }
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.None);
    }

    private static void ValidateRequest(PresentationWorkerRequest request)
    {
        ValidateFileName(request.FileName);
        if (!Path.GetExtension(request.FileName).Equals(
            ".pptx",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Presentation fileName must have a .pptx extension.",
                nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        if (request.Slides.Count is < 1 or > 7)
        {
            throw new ArgumentException(
                "A presentation requires between one and seven content slides.",
                nameof(request));
        }

        if (request.Slides.Any(static slide =>
            string.IsNullOrWhiteSpace(slide.Title)
            || string.IsNullOrWhiteSpace(slide.Body)))
        {
            throw new ArgumentException(
                "Every content slide requires a title and body.",
                nameof(request));
        }
    }

    private void ValidateManifest(
        PresentationWorkerRequest request,
        PresentationWorkerManifest manifest)
    {
        if (!manifest.ValidationPassed)
        {
            throw new IOException("The presentation worker reported failed validation.");
        }

        int expectedSlides = request.Slides.Count + 1;
        if (manifest.SlideCount != expectedSlides)
        {
            throw new IOException(
                $"The presentation worker returned {manifest.SlideCount} slides; "
                + $"{expectedSlides} were expected.");
        }

        if (manifest.Files.Count is < 4 || manifest.Files.Count > settings.MaximumFiles)
        {
            throw new IOException(
                "The presentation worker returned an invalid number of artifacts.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (PresentationWorkerFile file in manifest.Files)
        {
            ValidateFileName(file.FileName);
            if (file.SizeBytes is <= 0 || file.SizeBytes > settings.MaximumOutputBytes
                || file.Sha256.Length != 64
                || !file.Sha256.All(Uri.IsHexDigit)
                || !names.Add(file.FileName))
            {
                throw new IOException(
                    $"Presentation artifact '{file.FileName}' has invalid metadata.");
            }

            string expectedContentType = GetContentType(file.FileName);
            if (!file.ContentType.Equals(
                expectedContentType,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"Presentation artifact '{file.FileName}' has an invalid content type.");
            }
        }

        if (!manifest.Files.Any(file =>
                file.FileName.Equals(request.FileName, StringComparison.Ordinal))
            || !manifest.Files.Any(static file =>
                Path.GetExtension(file.FileName).Equals(
                    ".pdf",
                    StringComparison.OrdinalIgnoreCase))
            || manifest.Files.Count(static file =>
                Path.GetExtension(file.FileName).Equals(
                    ".png",
                    StringComparison.OrdinalIgnoreCase)) != expectedSlides
            || !manifest.Files.Any(static file =>
                Path.GetExtension(file.FileName).Equals(
                    ".json",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException(
                "The presentation worker did not return PPTX, PDF, slide previews, "
                + "and a validation manifest.");
        }
    }

    private static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName is "." or ".."
            || fileName.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            throw new IOException($"Unsafe presentation artifact name '{fileName}'.");
        }
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

    private static string Bound(string value) =>
        value.Length <= 2048 ? value : value[..2048];

    private static string GetContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pptx" =>
                "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".json" => "application/json",
            _ => throw new IOException(
                $"Presentation artifact '{fileName}' has an unsupported file type."),
        };

    private static PresentationExecutionResult ToResult(ExecutionJob job)
    {
        ExecutionArtifactReference[] outputs =
            string.IsNullOrWhiteSpace(job.OutputsJson)
                ? []
                : JsonSerializer.Deserialize<ExecutionArtifactReference[]>(
                    job.OutputsJson,
                    JsonOptions) ?? [];
        return new PresentationExecutionResult(
            job.JobId,
            job.Status.ToString(),
            job.Error,
            outputs);
    }
}

public sealed record PresentationExecutionResult(
    string JobId,
    string Status,
    string? Error,
    IReadOnlyList<ExecutionArtifactReference> Outputs);
