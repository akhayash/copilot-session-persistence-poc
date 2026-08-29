using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CopilotSessionPersistencePoc.AppState;
using CopilotSessionPersistencePoc.ArtifactStorage;
using CopilotSessionPersistencePoc.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Tests;

public sealed class PresentationExecutionCoordinatorTests
{
    [Fact]
    public async Task ValidatedPresentationArtifactsArePublished()
    {
        Dictionary<string, BinaryData> files = CreateFiles();
        PresentationWorkerManifest manifest = CreateManifest(files, slideCount: 2);
        var worker = new FakePresentationSessionsClient(manifest, files);
        var artifacts = new FakeArtifactStore();
        var jobs = new FakeExecutionJobRepository();
        PresentationExecutionCoordinator coordinator =
            CreateCoordinator(artifacts, jobs, worker);

        PresentationExecutionResult result = await coordinator.ExecuteAsync(
            "session-1",
            "tool-call-1",
            CreateRequest(),
            default);

        Assert.Equal("Succeeded", result.Status);
        Assert.Equal(5, result.Outputs.Count);
        Assert.Contains(result.Outputs, output => output.FileName == "deck.pptx");
        Assert.Contains(result.Outputs, output => output.FileName == "deck.pdf");
        Assert.Equal(
            2,
            result.Outputs.Count(output => output.FileName.EndsWith(
                ".png",
                StringComparison.Ordinal)));
        Assert.Contains(result.Outputs, output => output.FileName == "validation.json");
        Assert.Equal(1, worker.CreateCount);
        Assert.Equal(1, worker.StopCount);
    }

    [Fact]
    public async Task InvalidManifestFailsBeforePublishing()
    {
        Dictionary<string, BinaryData> files = CreateFiles();
        PresentationWorkerManifest valid = CreateManifest(files, slideCount: 2);
        PresentationWorkerFile first = valid.Files[0] with { Sha256 = "not-a-hash" };
        var worker = new FakePresentationSessionsClient(
            valid with { Files = [first, .. valid.Files.Skip(1)] },
            files);
        var artifacts = new FakeArtifactStore();
        var jobs = new FakeExecutionJobRepository();
        PresentationExecutionCoordinator coordinator =
            CreateCoordinator(artifacts, jobs, worker);

        await Assert.ThrowsAsync<IOException>(() =>
            coordinator.ExecuteAsync(
                "session-1",
                "tool-call-invalid",
                CreateRequest(),
                default));

        Assert.Empty(await artifacts.ListAsync("session-1"));
        Assert.Equal(ExecutionJobStatus.Failed, jobs.LastStatus);
        Assert.Equal(1, worker.StopCount);
    }

    [Fact]
    public async Task InvalidArtifactSignatureFailsBeforePublishing()
    {
        Dictionary<string, BinaryData> files = CreateFiles();
        files["deck.pdf"] = BinaryData.FromString("not-a-pdf");
        PresentationWorkerManifest manifest = CreateManifest(files, slideCount: 2);
        var worker = new FakePresentationSessionsClient(manifest, files);
        var artifacts = new FakeArtifactStore();
        var jobs = new FakeExecutionJobRepository();
        PresentationExecutionCoordinator coordinator =
            CreateCoordinator(artifacts, jobs, worker);

        await Assert.ThrowsAsync<IOException>(() =>
            coordinator.ExecuteAsync(
                "session-1",
                "tool-call-invalid-content",
                CreateRequest(),
                default));

        Assert.Empty(await artifacts.ListAsync("session-1"));
        Assert.Equal(ExecutionJobStatus.Failed, jobs.LastStatus);
    }

    [Fact]
    public async Task CleanupFailureDoesNotOverrideSuccessfulResult()
    {
        Dictionary<string, BinaryData> files = CreateFiles();
        PresentationWorkerManifest manifest = CreateManifest(files, slideCount: 2);
        var worker = new FakePresentationSessionsClient(
            manifest,
            files,
            new HttpRequestException("cleanup failed"));
        var artifacts = new FakeArtifactStore();
        var jobs = new FakeExecutionJobRepository();
        PresentationExecutionCoordinator coordinator =
            CreateCoordinator(artifacts, jobs, worker);

        PresentationExecutionResult result = await coordinator.ExecuteAsync(
            "session-1",
            "tool-call-cleanup-failure",
            CreateRequest(),
            default);

        Assert.Equal("Succeeded", result.Status);
        Assert.Equal(1, worker.StopCount);
    }

    private static PresentationExecutionCoordinator CreateCoordinator(
        IArtifactStore artifacts,
        IExecutionJobRepository jobs,
        IPresentationSessionsClient worker) =>
        new(
            new ExistingSessionRepository(),
            artifacts,
            jobs,
            worker,
            Options.Create(new PresentationSessionsOptions
            {
                Enabled = true,
                PoolManagementEndpoint = new Uri("https://example.invalid/"),
            }),
            NullLogger<PresentationExecutionCoordinator>.Instance);

    private static PresentationWorkerRequest CreateRequest() =>
        new(
            "deck.pptx",
            "Title",
            "Subtitle",
            "Leaders",
            [new PresentationSlide("Result", "Validated", "Passed")]);

    private static Dictionary<string, BinaryData> CreateFiles()
    {
        var files = new Dictionary<string, BinaryData>(StringComparer.Ordinal)
        {
            ["deck.pptx"] = CreatePptx(slideCount: 2),
            ["deck.pdf"] = BinaryData.FromString(
                "%PDF-1.7\n/Type /Page\n/Type /Page\n%%EOF"),
            ["slide-01.png"] = CreatePng(),
            ["slide-02.png"] = CreatePng(),
        };
        files["validation.json"] = BinaryData.FromString(
            JsonSerializer.Serialize(new
            {
                validationPassed = true,
                slideCount = 2,
                files = files.Select(pair => new
                {
                    fileName = pair.Key,
                    contentType = GetContentType(pair.Key),
                    sizeBytes = pair.Value.ToMemory().Length,
                    sha256 = Convert.ToHexStringLower(
                        SHA256.HashData(pair.Value.ToMemory().Span)),
                }),
            }));
        return files;
    }

    private static BinaryData CreatePptx(int slideCount)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(
                archive,
                "[Content_Types].xml",
                """<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>""");
            string slideIds = string.Concat(
                Enumerable.Range(1, slideCount)
                    .Select(index => $"""<p:sldId id="{255 + index}" r:id="rId{index}"/>"""));
            WriteEntry(
                archive,
                "ppt/presentation.xml",
                $"""<?xml version="1.0"?><p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><p:sldIdLst>{slideIds}</p:sldIdLst></p:presentation>""");
            string relationships = string.Concat(
                Enumerable.Range(1, slideCount)
                    .Select(index => $"""<Relationship Id="rId{index}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide{index}.xml"/>"""));
            WriteEntry(
                archive,
                "ppt/_rels/presentation.xml.rels",
                $"""<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{relationships}</Relationships>""");
            for (int index = 1; index <= slideCount; index++)
            {
                WriteEntry(
                    archive,
                    $"ppt/slides/slide{index}.xml",
                    """<?xml version="1.0"?><p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"/>""");
            }
        }

        return BinaryData.FromBytes(stream.ToArray());
    }

    private static BinaryData CreatePng() =>
        BinaryData.FromBytes(
            [
                0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
                0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
                0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
                0x08, 0x02, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4e, 0x44,
                0xae, 0x42, 0x60, 0x82,
            ]);

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        string content)
    {
        using Stream stream = archive.CreateEntry(name).Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
    }

    private static PresentationWorkerManifest CreateManifest(
        Dictionary<string, BinaryData> files,
        int slideCount) =>
        new(
            true,
            slideCount,
            files.Select(pair => new PresentationWorkerFile(
                pair.Key,
                GetContentType(pair.Key),
                pair.Value.ToMemory().Length,
                Convert.ToHexStringLower(
                    SHA256.HashData(pair.Value.ToMemory().Span))))
                .ToArray());

    private static string GetContentType(string fileName) =>
        Path.GetExtension(fileName) switch
        {
            ".pptx" =>
                "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".json" => "application/json",
            _ => "application/octet-stream",
        };

    private sealed class FakePresentationSessionsClient(
        PresentationWorkerManifest manifest,
        Dictionary<string, BinaryData> files,
        Exception? stopException = null)
        : IPresentationSessionsClient
    {
        public int CreateCount { get; private set; }

        public int StopCount { get; private set; }

        public Task<PresentationWorkerManifest> CreatePresentationAsync(
            string identifier,
            PresentationWorkerRequest request,
            CancellationToken cancellationToken)
        {
            CreateCount++;
            return Task.FromResult(manifest);
        }

        public Task<BinaryData> DownloadArtifactAsync(
            string identifier,
            string fileName,
            CancellationToken cancellationToken) =>
            Task.FromResult(files[fileName]);

        public Task StopSessionAsync(
            string identifier,
            CancellationToken cancellationToken)
        {
            StopCount++;
            if (stopException is not null)
            {
                return Task.FromException(stopException);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeArtifactStore : IArtifactStore
    {
        private readonly List<ArtifactInfo> items = [];

        public Task<IReadOnlyList<ArtifactInfo>> ListAsync(
            string sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ArtifactInfo>>(
                items.Where(item => item.SessionId == sessionId).ToArray());

        public Task<ArtifactInfo> PutAsync(
            string sessionId,
            string artifactId,
            string fileName,
            string contentType,
            BinaryData content,
            CancellationToken cancellationToken = default)
        {
            var item = new ArtifactInfo(
                sessionId,
                artifactId,
                fileName,
                contentType,
                Convert.ToHexStringLower(
                    SHA256.HashData(content.ToMemory().Span)),
                content.ToMemory().Length,
                new Uri($"https://example.invalid/{artifactId}/{fileName}"));
            items.Add(item);
            return Task.FromResult(item);
        }

        public Task<ArtifactContent?> GetAsync(
            string sessionId,
            string artifactId,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ArtifactContent?>(null);

        public Task DeleteAsync(
            string sessionId,
            string artifactId,
            CancellationToken cancellationToken = default)
        {
            items.RemoveAll(item =>
                item.SessionId == sessionId
                && item.ArtifactId == artifactId);
            return Task.CompletedTask;
        }

        public Task DeleteSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeExecutionJobRepository : IExecutionJobRepository
    {
        private ExecutionJob? job;

        public ExecutionJobStatus? LastStatus => job?.Status;

        public Task<ExecutionJob?> GetAsync(
            string toolCallId,
            CancellationToken cancellationToken) =>
            Task.FromResult(job);

        public Task<ExecutionJob?> GetByJobIdAsync(
            string sessionId,
            string jobId,
            CancellationToken cancellationToken) =>
            Task.FromResult(job);

        public Task<ExecutionJobReservation> GetOrCreateAsync(
            string sessionId,
            string toolCallId,
            string codeSha256,
            CancellationToken cancellationToken)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            job = new ExecutionJob(
                toolCallId,
                $"job-{Guid.NewGuid():N}",
                sessionId,
                toolCallId,
                codeSha256,
                ExecutionJobStatus.Pending,
                null,
                null,
                null,
                null,
                now,
                now,
                default);
            return Task.FromResult(new ExecutionJobReservation(job, true));
        }

        public Task<ExecutionJob> UpdateAsync(
            ExecutionJob current,
            ExecutionJobStatus status,
            string? standardOutput,
            string? standardError,
            string? failureMessage,
            string? outputsJson,
            CancellationToken cancellationToken)
        {
            job = current with
            {
                Status = status,
                StandardOutput = standardOutput,
                StandardError = standardError,
                Error = failureMessage,
                OutputsJson = outputsJson,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            return Task.FromResult(job);
        }

        public Task DeleteSessionAsync(
            string sessionId,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class ExistingSessionRepository : IAppSessionRepository
    {
        public Task<AppSession?> GetAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AppSession?>(new AppSession(
                id,
                "Test",
                "gpt-5-mini",
                true,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                0));

        public Task<IReadOnlyList<AppSession>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AppSession>>([]);

        public Task<bool> ExistsForDeletionAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<AppSession> CreateAsync(
            string id,
            string title,
            string model,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AppSession> MarkInitializedAsync(
            string id,
            long expectedVersion,
            string? title = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task TouchAsync(
            string id,
            long expectedVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
