using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Persistence;

public sealed class AzureStorageInitializer(
    AzureStorageClients clients,
    IOptions<AzureStorageOptions> options)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        AzureStorageOptions value = options.Value;
        await clients.BlobService
            .GetBlobContainerClient(value.SessionFsContainer)
            .CreateIfNotExistsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await clients.BlobService
            .GetBlobContainerClient(value.SessionLocksContainer)
            .CreateIfNotExistsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await clients.BlobService
            .GetBlobContainerClient(value.ArtifactsContainer)
            .CreateIfNotExistsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await clients.TableService
            .GetTableClient(value.AppSessionsTable)
            .CreateIfNotExistsAsync(cancellationToken)
            .ConfigureAwait(false);
        await clients.TableService
            .GetTableClient(value.ExecutionJobsTable)
            .CreateIfNotExistsAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
