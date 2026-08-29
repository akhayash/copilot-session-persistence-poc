using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using CopilotSessionPersistencePoc.AppState;
using CopilotSessionPersistencePoc.Persistence;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.ArtifactStorage;

public sealed class AzureBlobArtifactStore(
    AzureStorageClients clients,
    ISessionOwnerContext ownerContext,
    IOptions<AzureStorageOptions> options)
    : IArtifactStore
{
    private readonly BlobContainerClient container =
        clients.BlobService.GetBlobContainerClient(options.Value.ArtifactsContainer);

    public async Task<IReadOnlyList<ArtifactInfo>> ListAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ValidateSegment(sessionId, nameof(sessionId));
        string prefix = GetSessionPrefix(sessionId);
        var artifacts = new List<ArtifactInfo>();
        await foreach (BlobItem blob in container.GetBlobsAsync(
            BlobTraits.Metadata,
            BlobStates.None,
            prefix,
            cancellationToken))
        {
            string relativeName = blob.Name[prefix.Length..];
            string[] parts = relativeName.Split('/', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            string artifactId = Uri.UnescapeDataString(parts[0]);
            string fileName = Uri.UnescapeDataString(parts[1]);
            string sha256 = blob.Metadata.TryGetValue("sha256", out string? storedHash)
                ? storedHash
                : string.Empty;
            artifacts.Add(new ArtifactInfo(
                sessionId,
                artifactId,
                fileName,
                blob.Properties.ContentType ?? "application/octet-stream",
                sha256,
                blob.Properties.ContentLength ?? 0,
                container.GetBlobClient(blob.Name).Uri));
        }

        return artifacts
            .OrderBy(static artifact => artifact.ArtifactId, StringComparer.Ordinal)
            .ThenBy(static artifact => artifact.FileName, StringComparer.Ordinal)
            .ToArray();
    }

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
            ["ownerid"] = ownerContext.OwnerKey,
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

    public async Task DeleteAsync(
        string sessionId,
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        ValidateSegment(sessionId, nameof(sessionId));
        ValidateSegment(artifactId, nameof(artifactId));
        string prefix = $"{GetSessionPrefix(sessionId)}{Uri.EscapeDataString(artifactId)}/";
        await foreach (BlobItem blob in container.GetBlobsAsync(
            BlobTraits.None,
            BlobStates.None,
            prefix: prefix,
            cancellationToken: cancellationToken))
        {
            await container.DeleteBlobIfExistsAsync(
                blob.Name,
                DeleteSnapshotsOption.IncludeSnapshots,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ValidateSegment(sessionId, nameof(sessionId));
        string prefix = GetSessionPrefix(sessionId);
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
            $"{GetSessionPrefix(sessionId)}"
            + $"{Uri.EscapeDataString(artifactId)}/{Uri.EscapeDataString(fileName)}");

    private string GetSessionPrefix(string sessionId) =>
        $"owners/{ownerContext.OwnerKey}/sessions/{Uri.EscapeDataString(sessionId)}/artifacts/";

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
