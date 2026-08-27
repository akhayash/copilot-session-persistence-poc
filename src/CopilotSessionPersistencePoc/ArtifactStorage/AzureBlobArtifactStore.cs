using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using CopilotSessionPersistencePoc.Persistence;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.ArtifactStorage;

public sealed class AzureBlobArtifactStore(
    AzureStorageClients clients,
    IOptions<AzureStorageOptions> options)
    : IArtifactStore
{
    private readonly BlobContainerClient container =
        clients.BlobService.GetBlobContainerClient(options.Value.ArtifactsContainer);

    public async Task<ArtifactInfo> PutAsync(
        string sessionId,
        string artifactId,
        string fileName,
        string contentType,
        BinaryData content,
        CancellationToken cancellationToken = default)
    {
        ValidateSegment(sessionId, nameof(sessionId));
        ValidateSegment(artifactId, nameof(artifactId));
        ValidateFileName(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(content);

        string sha256 = Convert.ToHexStringLower(SHA256.HashData(content.ToMemory().Span));
        string storedContentType = contentType;
        BlobClient blob = GetBlob(sessionId, artifactId, fileName);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sessionid"] = sessionId,
            ["artifactid"] = artifactId,
            ["filename"] = fileName,
            ["sha256"] = sha256,
        };
        try
        {
            await blob.UploadAsync(
                    content,
                    new BlobUploadOptions
                    {
                        Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                        HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                        Metadata = metadata,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            BlobProperties existing =
                (await blob.GetPropertiesAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false)).Value;
            if (!existing.Metadata.TryGetValue("sha256", out string? existingHash)
                || !string.Equals(existingHash, sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"Artifact '{artifactId}/{fileName}' already exists with different content.",
                    exception);
            }

            storedContentType = existing.ContentType;
        }

        return new ArtifactInfo(
            sessionId,
            artifactId,
            fileName,
            storedContentType,
            sha256,
            content.ToMemory().Length,
            blob.Uri);
    }

    public async Task<ArtifactContent?> GetAsync(
        string sessionId,
        string artifactId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ValidateSegment(sessionId, nameof(sessionId));
        ValidateSegment(artifactId, nameof(artifactId));
        ValidateFileName(fileName);
        BlobClient blob = GetBlob(sessionId, artifactId, fileName);
        try
        {
            BlobDownloadResult result =
                await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            string sha256 = result.Details.Metadata.TryGetValue("sha256", out string? storedHash)
                ? storedHash
                : Convert.ToHexStringLower(SHA256.HashData(result.Content.ToMemory().Span));
            return new ArtifactContent(
                new ArtifactInfo(
                    sessionId,
                    artifactId,
                    fileName,
                    result.Details.ContentType ?? "application/octet-stream",
                    sha256,
                    result.Content.ToMemory().Length,
                    blob.Uri),
                result.Content);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ValidateSegment(sessionId, nameof(sessionId));
        string prefix = $"sessions/{Uri.EscapeDataString(sessionId)}/artifacts/";
        await foreach (BlobItem blob in container.GetBlobsAsync(
            BlobTraits.None,
            BlobStates.None,
            prefix,
            cancellationToken))
        {
            await container.DeleteBlobIfExistsAsync(
                    blob.Name,
                    DeleteSnapshotsOption.IncludeSnapshots,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private BlobClient GetBlob(string sessionId, string artifactId, string fileName) =>
        container.GetBlobClient(
            $"sessions/{Uri.EscapeDataString(sessionId)}/artifacts/"
            + $"{Uri.EscapeDataString(artifactId)}/{Uri.EscapeDataString(fileName)}");

    private static void ValidateSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value is "." or ".." || value.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            throw new ArgumentException(
                "Artifact identifiers cannot be dot segments or contain path separators or NUL.",
                parameterName);
        }
    }

    private static void ValidateFileName(string fileName)
    {
        ValidateSegment(fileName, nameof(fileName));
    }
}
