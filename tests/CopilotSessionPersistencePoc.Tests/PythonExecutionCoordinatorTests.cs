using System.Security.Cryptography;
using CopilotSessionPersistencePoc.AppState;
using CopilotSessionPersistencePoc.ArtifactStorage;
using CopilotSessionPersistencePoc.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Tests;

public sealed class PythonExecutionCoordinatorTests
{
    [Fact]
    public async Task ExecutionPublishesChangedOutputsAndIsIdempotent()
    {
        var artifactStore = new FakeArtifactStore();
        await artifactStore.PutAsync(
            "session-1",
            "upload-1",
            "input.csv",
            "text/csv",
            BinaryData.FromString("value\n1\n2\n"));
        var jobs = new FakeExecutionJobRepository();
        var sandbox = new FakeDynamicSessionsClient(
            new Dictionary<string, BinaryData>(StringComparer.Ordinal)
            {
                ["input.csv"] = BinaryData.FromString("value\n1\n2\n"),
                ["result.csv"] = BinaryData.FromString("sum\n3\n"),
            });
        PythonExecutionCoordinator coordinator = CreateCoordinator(
            artifactStore,
            jobs,
            sandbox);

        PythonExecutionResult first = await coordinator.ExecuteAsync(
            "session-1",
            "tool-call-1",
            "print('done')",
            ["upload-1/input.csv"],
            default);
        PythonExecutionResult repeated = await coordinator.ExecuteAsync(
            "session-1",
            "tool-call-1",
            "print('done')",
            ["upload-1/input.csv"],
            default);

        Assert.Equal("Succeeded", first.Status);
        Assert.Equal("done\n", first.StandardOutput);
        ExecutionArtifactReference output = Assert.Single(first.Outputs);
        Assert.Equal("result.csv", output.FileName);
        Assert.Equal(first.JobId, repeated.JobId);
        Assert.Equal(first.Status, repeated.Status);
        Assert.Equal(first.StandardOutput, repeated.StandardOutput);
        Assert.Equal(first.Outputs, repeated.Outputs);
        Assert.Equal(1, sandbox.ExecutionCount);
        Assert.Single(sandbox.DeletedIdentifiers);
        Assert.DoesNotContain(
            sandbox.DeletedIdentifiers[0],
            first.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedExecutionPersistsFailureWithoutPublishingOutput()
    {
        var jobs = new FakeExecutionJobRepository();
        var sandbox = new FakeDynamicSessionsClient(
            new Dictionary<string, BinaryData>(StringComparer.Ordinal),
            status: "Failed");
        PythonExecutionCoordinator coordinator = CreateCoordinator(
            new FakeArtifactStore(),
            jobs,
            sandbox);

        PythonExecutionResult result = await coordinator.ExecuteAsync(
            "session-1",
            "tool-call-failed",
            "raise RuntimeError('no')",
            [],
            default);

        Assert.Equal("Failed", result.Status);
        Assert.Contains("reported status", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Outputs);
    }

    [Fact]
    public async Task TimedOutExecutionPersistsTimedOutStatus()
    {
        var jobs = new FakeExecutionJobRepository();
        var sandbox = new FakeDynamicSessionsClient(
            new Dictionary<string, BinaryData>(StringComparer.Ordinal),
            status: "TimedOut");
        PythonExecutionCoordinator coordinator = CreateCoordinator(
            new FakeArtifactStore(),
            jobs,
            sandbox);

        PythonExecutionResult result = await coordinator.ExecuteAsync(
            "session-1",
            "tool-call-timeout",
            "while True: pass",
            [],
            default);

        Assert.Equal("TimedOut", result.Status);
        Assert.Empty(result.Outputs);
    }

    [Fact]
    public async Task CanceledExecutionPersistsCancelledStatus()
    {
        var sandbox = new FakeDynamicSessionsClient(
            new Dictionary<string, BinaryData>(StringComparer.Ordinal),
            status: "Canceled");
        PythonExecutionCoordinator coordinator = CreateCoordinator(
            new FakeArtifactStore(),
            new FakeExecutionJobRepository(),
            sandbox);

        PythonExecutionResult result = await coordinator.ExecuteAsync(
            "session-1",
            "tool-call-canceled",
            "print('cancel')",
            [],
            default);

        Assert.Equal("Cancelled", result.Status);
    }

    [Fact]
    public async Task ArtifactListHidesOutputsUnlessTheirJobSucceeded()
    {
        var artifacts = new FakeArtifactStore();
        var jobs = new FakeExecutionJobRepository();
        ExecutionJob job = jobs.Seed(
            "session-1",
            "tool-call-output",
            ExecutionJobStatus.Failed);
        await artifacts.PutAsync(
            "session-1",
            job.JobId,
            "partial.csv",
            "text/csv",
            BinaryData.FromString("partial"));
        PythonExecutionCoordinator coordinator = CreateCoordinator(
            artifacts,
            jobs,
            new FakeDynamicSessionsClient([]));

        Assert.Empty(await coordinator.ListArtifactsAsync("session-1", default));

        await jobs.UpdateAsync(
            job,
            ExecutionJobStatus.Succeeded,
            string.Empty,
            string.Empty,
            null,
            "[]",
            default);
        ExecutionArtifactReference published = Assert.Single(
            await coordinator.ListArtifactsAsync("session-1", default));
        Assert.Equal("partial.csv", published.FileName);
    }

    [Fact]
    public async Task DuplicateInvocationWaitsForExistingJobToFinish()
    {
        var jobs = new FakeExecutionJobRepository();
        ExecutionJob existing = jobs.Seed(
            "session-1",
            "tool-call-running",
            ExecutionJobStatus.Running);
        var sandbox = new FakeDynamicSessionsClient([]);
        PythonExecutionCoordinator coordinator = CreateCoordinator(
            new FakeArtifactStore(),
            jobs,
            sandbox);

        Task<PythonExecutionResult> execution = coordinator.ExecuteAsync(
            "session-1",
            existing.ToolCallId,
            "print('done')",
            [],
            default);
        await Task.Delay(100);
        await jobs.UpdateAsync(
            existing,
            ExecutionJobStatus.Succeeded,
            "existing result",
            string.Empty,
            null,
            "[]",
            default);

        PythonExecutionResult result = await execution;

        Assert.Equal("Succeeded", result.Status);
        Assert.Equal("existing result", result.StandardOutput);
        Assert.Equal(0, sandbox.ExecutionCount);
    }

    [Fact]
    public async Task StaleDuplicateFailsWithoutReexecutingCode()
    {
        var jobs = new FakeExecutionJobRepository();
        ExecutionJob existing = jobs.Seed(
            "session-1",
            "tool-call-stale",
            ExecutionJobStatus.Running,
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));
        var sandbox = new FakeDynamicSessionsClient([]);
        PythonExecutionCoordinator coordinator = CreateCoordinator(
            new FakeArtifactStore(),
            jobs,
            sandbox,
            TimeSpan.FromMinutes(5));

        PythonExecutionResult result = await coordinator.ExecuteAsync(
            "session-1",
            existing.ToolCallId,
            "print('done')",
            [],
            default);

        Assert.Equal("Failed", result.Status);
        Assert.Contains("stopped", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, sandbox.ExecutionCount);
    }

    private static PythonExecutionCoordinator CreateCoordinator(
        IArtifactStore artifacts,
        IExecutionJobRepository jobs,
        IDynamicSessionsClient dynamicSessions,
        TimeSpan? staleJobTimeout = null) =>
        new(
            new ExistingSessionRepository(),
            artifacts,
            jobs,
            dynamicSessions,
            Options.Create(new DynamicSessionsOptions
            {
                Enabled = true,
                PoolManagementEndpoint = new Uri("https://example.invalid/"),
                StaleJobTimeout = staleJobTimeout ?? TimeSpan.FromMinutes(5),
            }),
            NullLogger<PythonExecutionCoordinator>.Instance);

    private sealed class ExistingSessionRepository : IAppSessionRepository
    {
        public Task<IReadOnlyList<AppSession>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AppSession>>([]);

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

    private sealed class FakeArtifactStore : IArtifactStore
    {
        private readonly Dictionary<string, ArtifactContent> contents =
            new(StringComparer.Ordinal);

        public Task<IReadOnlyList<ArtifactInfo>> ListAsync(
            string sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ArtifactInfo>>(
                contents.Values
                    .Where(item => item.Info.SessionId == sessionId)
                    .Select(static item => item.Info)
                    .ToArray());

        public Task<ArtifactInfo> PutAsync(
            string sessionId,
            string artifactId,
            string fileName,
            string contentType,
            BinaryData content,
            CancellationToken cancellationToken = default)
        {
            string hash = Convert.ToHexStringLower(
                SHA256.HashData(content.ToMemory().Span));
            var info = new ArtifactInfo(
                sessionId,
                artifactId,
                fileName,
                contentType,
                hash,
                content.ToMemory().Length,
                new Uri($"https://example.invalid/{artifactId}/{fileName}"));
            contents[$"{sessionId}/{artifactId}/{fileName}"] =
                new ArtifactContent(info, content);
            return Task.FromResult(info);
        }

        public Task<ArtifactContent?> GetAsync(
            string sessionId,
            string artifactId,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            contents.TryGetValue(
                $"{sessionId}/{artifactId}/{fileName}",
                out ArtifactContent? content);
            return Task.FromResult(content);
        }

        public Task DeleteSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeExecutionJobRepository : IExecutionJobRepository
    {
        private readonly Dictionary<string, ExecutionJob> jobs =
            new(StringComparer.Ordinal);

        public ExecutionJob Seed(
            string sessionId,
            string toolCallId,
            ExecutionJobStatus status,
            DateTimeOffset? updatedAt = null)
        {
            DateTimeOffset now = updatedAt ?? DateTimeOffset.UtcNow;
            var job = new ExecutionJob(
                toolCallId,
                $"job-{Guid.NewGuid():N}",
                sessionId,
                toolCallId,
                "code-hash",
                status,
                null,
                null,
                null,
                null,
                now,
                now,
                default);
            jobs.Add(toolCallId, job);
            return job;
        }

        public Task<ExecutionJob?> GetAsync(
            string toolCallId,
            CancellationToken cancellationToken)
        {
            jobs.TryGetValue(toolCallId, out ExecutionJob? job);
            return Task.FromResult(job);
        }

        public Task<ExecutionJob?> GetByJobIdAsync(
            string sessionId,
            string jobId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                jobs.Values.SingleOrDefault(
                    job => job.SessionId == sessionId && job.JobId == jobId));

        public Task<ExecutionJobReservation> GetOrCreateAsync(
            string sessionId,
            string toolCallId,
            string codeSha256,
            CancellationToken cancellationToken)
        {
            if (jobs.TryGetValue(toolCallId, out ExecutionJob? existing))
            {
                return Task.FromResult(new ExecutionJobReservation(existing, false));
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var created = new ExecutionJob(
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
            jobs.Add(toolCallId, created);
            return Task.FromResult(new ExecutionJobReservation(created, true));
        }

        public Task<ExecutionJob> UpdateAsync(
            ExecutionJob job,
            ExecutionJobStatus status,
            string? standardOutput,
            string? standardError,
            string? failureMessage,
            string? outputsJson,
            CancellationToken cancellationToken)
        {
            ExecutionJob updated = job with
            {
                Status = status,
                StandardOutput = standardOutput,
                StandardError = standardError,
                Error = failureMessage,
                OutputsJson = outputsJson,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            jobs[job.ToolCallId] = updated;
            return Task.FromResult(updated);
        }

        public Task DeleteSessionAsync(
            string sessionId,
            CancellationToken cancellationToken)
        {
            foreach (string key in jobs
                .Where(pair => pair.Value.SessionId == sessionId)
                .Select(static pair => pair.Key)
                .ToArray())
            {
                jobs.Remove(key);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeDynamicSessionsClient(
        Dictionary<string, BinaryData> files,
        string status = "Succeeded")
        : IDynamicSessionsClient
    {
        public int ExecutionCount { get; private set; }

        public List<string> DeletedIdentifiers { get; } = [];

        public Task UploadFileAsync(
            string identifier,
            string fileName,
            string contentType,
            BinaryData content,
            CancellationToken cancellationToken)
        {
            files[fileName] = content;
            return Task.CompletedTask;
        }

        public Task<DynamicSessionExecutionResult> ExecuteCodeAsync(
            string identifier,
            string code,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(new DynamicSessionExecutionResult(
                status,
                "done\n",
                string.Empty,
                string.Empty,
                5));
        }

        public Task<IReadOnlyList<DynamicSessionFile>> ListFilesAsync(
            string identifier,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DynamicSessionFile>>(
                files.Select(pair => new DynamicSessionFile(
                    pair.Key,
                    pair.Value.ToMemory().Length,
                    DateTimeOffset.UtcNow))
                .ToArray());

        public Task<BinaryData> DownloadFileAsync(
            string identifier,
            string fileName,
            CancellationToken cancellationToken) =>
            Task.FromResult(files[fileName]);

        public Task DeleteSessionAsync(
            string identifier,
            CancellationToken cancellationToken)
        {
            DeletedIdentifiers.Add(identifier);
            return Task.CompletedTask;
        }
    }
}
