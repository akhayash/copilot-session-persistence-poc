using Azure.Core;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Persistence;

public sealed class AzureStorageClients
{
    public AzureStorageClients(IOptions<AzureStorageOptions> options, TokenCredential credential)
    {
        AzureStorageOptions value = options.Value;
        if (!string.IsNullOrWhiteSpace(value.ConnectionString))
        {
            BlobService = new BlobServiceClient(value.ConnectionString);
            TableService = new TableServiceClient(value.ConnectionString);
            return;
        }

        BlobService = new BlobServiceClient(
            value.BlobServiceUri
                ?? throw new InvalidOperationException(
                    "AzureStorage:BlobServiceUri is required without a connection string."),
            credential);
        TableService = new TableServiceClient(
            value.TableServiceUri
                ?? throw new InvalidOperationException(
                    "AzureStorage:TableServiceUri is required without a connection string."),
            credential);
    }

    public BlobServiceClient BlobService { get; }

    public TableServiceClient TableService { get; }
}
