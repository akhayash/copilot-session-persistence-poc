using Azure.Identity;
using CopilotSessionPersistencePoc.AppState;
using CopilotSessionPersistencePoc.ArtifactStorage;
using CopilotSessionPersistencePoc.Copilot;
using CopilotSessionPersistencePoc.Execution;
using CopilotSessionPersistencePoc.Persistence;
using CopilotSessionPersistencePoc.SessionFs;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Tests;

public sealed class AzureStorageMultiNodeIntegrationTests
{
    [Theory]
    [InlineData(".", "artifact")]
    [InlineData("..", "artifact")]
    [InlineData("session", ".")]
    [InlineData("session", "..")]
    public async Task ArtifactStoreRejectsDotSegments(string sessionId, string artifactId)
    {
        var options = Options.Create(
            new AzureStorageOptions
            {
                ConnectionString = "UseDevelopmentStorage=true",
            });
        var clients = new AzureStorageClients(options, new DefaultAzureCredential());
        var store = new AzureBlobArtifactStore(
            clients,
            new TestSessionOwnerContext("path-test-user"),
            options);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.PutAsync(
                sessionId,
                artifactId,
                "report.md",
                "text/markdown",
                BinaryData.FromString("# Report\n")));
    }

    [AzureStorageFact]
    public async Task SeparateNodesShareConversationMetadataArtifactsAndLock()
    {
        string suffix = Guid.NewGuid().ToString("N")[..16];
        string? connectionString =
            Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING");
        var options = Options.Create(
            new AzureStorageOptions
            {
                ConnectionString = connectionString,
                BlobServiceUri = GetServiceUri(
                    connectionString,
                    "AZURE_STORAGE_BLOB_SERVICE_URI"),
                TableServiceUri = GetServiceUri(
                    connectionString,
                    "AZURE_STORAGE_TABLE_SERVICE_URI"),
                SessionFsContainer = $"sessionfs-{suffix}",
                SessionLocksContainer = $"locks-{suffix}",
                ArtifactsContainer = $"artifacts-{suffix}",
                AppSessionsTable = $"appsessions{suffix}",
                ExecutionJobsTable = $"executionjobs{suffix}",
            });
        var clients = new AzureStorageClients(options, new DefaultAzureCredential());
        var initializer = new AzureStorageInitializer(clients, options);
        await initializer.InitializeAsync();

        try
        {
            var nodeAStore = new AzureBlobSessionFsStore(clients, options);
            var nodeBStore = new AzureBlobSessionFsStore(clients, options);
            ISessionFsHandler nodeA =
                new AzureBlobSessionFsProvider(nodeAStore, "shared-session");
            ISessionFsHandler nodeB =
                new AzureBlobSessionFsProvider(nodeBStore, "shared-session");

            Assert.Null(await nodeA.WriteFileAsync(new SessionFsWriteFileRequest
            {
                Path = "/session-state/events.jsonl",
                Content = "{\"node\":\"A\",\"marker\":\"COBALT-731\"}\n",
            }, default));
            var restored =
                await nodeB.ReadFileAsync(new SessionFsReadFileRequest
                {
                    Path = "/session-state/events.jsonl",
                }, default);
            Assert.Contains("COBALT-731", restored.Content, StringComparison.Ordinal);

            Task[] appends = Enumerable.Range(0, 6)
                .Select(index =>
                {
                    ISessionFsHandler provider = index % 2 == 0 ? nodeA : nodeB;
                    return provider.AppendFileAsync(new SessionFsAppendFileRequest
                    {
                        Path = "/session-state/events.jsonl",
                        Content = $"{{\"append\":{index}}}\n",
                    }, default);
                })
                .ToArray();
            await Task.WhenAll(appends);
            var appended =
                await nodeA.ReadFileAsync(new SessionFsReadFileRequest
                {
                    Path = "/session-state/events.jsonl",
                }, default);
            foreach (int index in Enumerable.Range(0, 6))
            {
                Assert.Equal(
                    1,
                    appended.Content.Split(
                        $"\"append\":{index}",
                        StringSplitOptions.None).Length - 1);
            }

            var sharedOwner = new TestSessionOwnerContext("shared-user");
            var sharedJobs = new AzureTableExecutionJobRepository(
                clients,
                sharedOwner,
                options);
            var repositoryA =
                new AzureTableAppSessionRepository(
                    clients,
                    nodeAStore,
                    new AzureBlobArtifactStore(clients, sharedOwner, options),
                    sharedJobs,
                    sharedOwner,
                    options);
            var repositoryB =
                new AzureTableAppSessionRepository(
                    clients,
                    nodeBStore,
                    new AzureBlobArtifactStore(clients, sharedOwner, options),
                    sharedJobs,
                    sharedOwner,
                    options);
            AppSession created = await repositoryA.CreateAsync(
                "shared-session",
                "Multi-node validation",
                "gpt-5-mini");
            AppSession? observed = await repositoryB.GetAsync("shared-session");
            Assert.Equal(created, observed);
            AppSession initialized =
                await repositoryB.MarkInitializedAsync(
                    "shared-session",
                    observed!.Version,
                    "Generated title");
            Assert.True((await repositoryA.GetAsync("shared-session"))!.IsInitialized);
            Assert.Equal(
                "Generated title",
                (await repositoryA.GetAsync("shared-session"))!.Title);
            Assert.Equal(1, initialized.Version);

            var otherOwner = new TestSessionOwnerContext("different-user");
            var otherOwnerRepository =
                new AzureTableAppSessionRepository(
                    clients,
                    nodeBStore,
                    new AzureBlobArtifactStore(clients, otherOwner, options),
                    new AzureTableExecutionJobRepository(
                        clients,
                        otherOwner,
                        options),
                    otherOwner,
                    options);
            Assert.Empty(await otherOwnerRepository.ListAsync());
            Assert.Null(await otherOwnerRepository.GetAsync("shared-session"));
            Assert.False(
                await otherOwnerRepository.ExistsForDeletionAsync("shared-session"));
            await otherOwnerRepository.DeleteAsync("shared-session");
            Assert.NotNull(await repositoryA.GetAsync("shared-session"));

            await repositoryA.CreateAsync(
                "deletion-retry-session",
                "Deletion retry",
                "gpt-5-mini");
            var table = clients.TableService.GetTableClient(options.Value.AppSessionsTable);
            var deletingEntity = (
                await table.GetEntityAsync<AzureTableAppSessionRepository.AppSessionEntity>(
                    sharedOwner.OwnerKey,
                    "deletion-retry-session")).Value;
            deletingEntity.IsDeleting = true;
            await table.UpdateEntityAsync(
                deletingEntity,
                deletingEntity.ETag,
                Azure.Data.Tables.TableUpdateMode.Replace);
            Assert.Null(await repositoryB.GetAsync("deletion-retry-session"));
            Assert.True(await repositoryB.ExistsForDeletionAsync("deletion-retry-session"));
            await repositoryB.DeleteAsync("deletion-retry-session");
            Assert.False(await repositoryA.ExistsForDeletionAsync("deletion-retry-session"));

            var artifactA = new AzureBlobArtifactStore(
                clients,
                sharedOwner,
                options);
            var artifactB = new AzureBlobArtifactStore(
                clients,
                sharedOwner,
                options);
            ArtifactInfo uploaded = await artifactA.PutAsync(
                "shared-session",
                "report-001",
                "summary.md",
                "text/markdown",
                BinaryData.FromString("# Shared artifact\n"));
            ArtifactContent? downloaded = await artifactB.GetAsync(
                "shared-session",
                "report-001",
                "summary.md");
            Assert.NotNull(downloaded);
            Assert.Equal(uploaded.Sha256, downloaded.Info.Sha256);
            Assert.Equal("# Shared artifact\n", downloaded.Content.ToString());
            ArtifactInfo idempotent = await artifactB.PutAsync(
                "shared-session",
                "report-001",
                "summary.md",
                "application/octet-stream",
                BinaryData.FromString("# Shared artifact\n"));
            Assert.Equal("text/markdown", idempotent.ContentType);
            var otherOwnerArtifacts = new AzureBlobArtifactStore(
                clients,
                otherOwner,
                options);
            Assert.Empty(await otherOwnerArtifacts.ListAsync("shared-session"));
            Assert.Null(await otherOwnerArtifacts.GetAsync(
                "shared-session",
                "report-001",
                "summary.md"));

            ExecutionJobReservation jobA = await sharedJobs.GetOrCreateAsync(
                "shared-session",
                "tool-call-001",
                "code-hash",
                default);
            ExecutionJobReservation jobB = await sharedJobs.GetOrCreateAsync(
                "shared-session",
                "tool-call-001",
                "code-hash",
                default);
            Assert.True(jobA.Created);
            Assert.False(jobB.Created);
            Assert.Equal(jobA.Job.JobId, jobB.Job.JobId);
            ExecutionJob completed = await sharedJobs.UpdateAsync(
                jobA.Job,
                ExecutionJobStatus.Succeeded,
                "complete",
                string.Empty,
                null,
                "[]",
                default);
            Assert.Equal(ExecutionJobStatus.Succeeded, completed.Status);
            await Assert.ThrowsAsync<ExecutionJobConcurrencyException>(
                () => sharedJobs.UpdateAsync(
                    jobB.Job,
                    ExecutionJobStatus.Failed,
                    null,
                    null,
                    "stale waiter",
                    null,
                    default));

            var lockA = new AzureBlobSessionLockProvider(
                clients,
                options,
                NullLogger<AzureBlobSessionLockProvider>.Instance);
            var lockB = new AzureBlobSessionLockProvider(
                clients,
                options,
                NullLogger<AzureBlobSessionLockProvider>.Instance);
            await using (IAsyncDisposable leaseA =
                await lockA.TryAcquireAsync("shared-session", default))
            {
                await Assert.ThrowsAsync<SessionBusyException>(
                    () => lockB.TryAcquireAsync("shared-session", default));
            }

            await using (ISessionLockHandle leaseB =
                await lockB.TryAcquireAsync("shared-session", default))
            {
                leaseB.DeleteOnRelease();
            }
            var remainingLocks = new List<string>();
            await foreach (var blob in clients.BlobService
                .GetBlobContainerClient(options.Value.SessionLocksContainer)
                .GetBlobsAsync())
            {
                remainingLocks.Add(blob.Name);
            }
            Assert.Empty(remainingLocks);

            await repositoryA.DeleteAsync("shared-session");
            Assert.Null(await repositoryB.GetAsync("shared-session"));
            Assert.False(await nodeBStore.ExistsAsync("shared-session"));
            Assert.Null(await artifactB.GetAsync(
                "shared-session",
                "report-001",
                "summary.md"));
            var jobRows = new List<AzureTableExecutionJobRepository.ExecutionJobEntity>();
            await foreach (var entity in clients.TableService
                .GetTableClient(options.Value.ExecutionJobsTable)
                .QueryAsync<AzureTableExecutionJobRepository.ExecutionJobEntity>(
                    entity => entity.PartitionKey == sharedOwner.OwnerKey
                        && entity.SessionId == "shared-session"))
            {
                jobRows.Add(entity);
            }
            Assert.Empty(jobRows);
        }
        finally
        {
            AzureStorageOptions value = options.Value;
            await clients.BlobService
                .GetBlobContainerClient(value.SessionFsContainer)
                .DeleteIfExistsAsync();
            await clients.BlobService
                .GetBlobContainerClient(value.SessionLocksContainer)
                .DeleteIfExistsAsync();
            await clients.BlobService
                .GetBlobContainerClient(value.ArtifactsContainer)
                .DeleteIfExistsAsync();
            await clients.TableService
                .GetTableClient(value.AppSessionsTable)
                .DeleteAsync();
            await clients.TableService
                .GetTableClient(value.ExecutionJobsTable)
                .DeleteAsync();
        }
    }

    private sealed class AzureStorageFactAttribute : FactAttribute
    {
        public AzureStorageFactAttribute()
        {
            bool hasConnectionString = !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING"));
            bool hasServiceUris = !string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("AZURE_STORAGE_BLOB_SERVICE_URI"))
                && !string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("AZURE_STORAGE_TABLE_SERVICE_URI"));
            if (!hasConnectionString && !hasServiceUris)
            {
                Skip =
                    "Azure Storage connection string or Blob/Table service URIs are required.";
            }
        }
    }

    private static Uri? GetServiceUri(string? connectionString, string variableName) =>
        string.IsNullOrWhiteSpace(connectionString)
            ? new Uri(
                Environment.GetEnvironmentVariable(variableName)
                    ?? throw new InvalidOperationException(
                        $"Environment variable '{variableName}' is required."))
            : null;
}
