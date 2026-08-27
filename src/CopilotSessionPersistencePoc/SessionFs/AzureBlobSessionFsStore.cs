using System.Diagnostics;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using CopilotSessionPersistencePoc.Persistence;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.SessionFs;

public sealed class AzureBlobSessionFsStore(
    AzureStorageClients clients,
    IOptions<AzureStorageOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly BlobContainerClient container =
        clients.BlobService.GetBlobContainerClient(options.Value.SessionFsContainer);
    private readonly int maximumWriteAttempts = options.Value.MaximumWriteAttempts;

    public Uri GetStateUri(string sessionId) => GetBlob(sessionId).Uri;

    public async Task<long?> GetStateSizeAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return (await GetBlob(sessionId)
                    .GetPropertiesAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false))
                .Value.ContentLength;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        (await GetBlob(sessionId).ExistsAsync(cancellationToken).ConfigureAwait(false)).Value;

    public async Task<AzureSessionFsState> ReadAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        (await ReadVersionedAsync(sessionId, cancellationToken).ConfigureAwait(false)).State;

    public async Task MutateAsync(
        string sessionId,
        Action<AzureSessionFsState> mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        Guid mutationId = Guid.NewGuid();

        for (var attempt = 1; attempt <= maximumWriteAttempts; attempt++)
        {
            VersionedState current =
                await ReadVersionedAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (current.State.AppliedMutationIds.Contains(mutationId))
            {
                return;
            }

            mutation(current.State);
            current.State.Version++;
            current.State.AppliedMutationIds.Add(mutationId);

            var conditions = current.ETag is { } etag
                ? new BlobRequestConditions { IfMatch = etag }
                : new BlobRequestConditions { IfNoneMatch = ETag.All };
            var uploadOptions = new BlobUploadOptions
            {
                Conditions = conditions,
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
            };

            try
            {
                BinaryData content = BinaryData.FromObjectAsJson(current.State, JsonOptions);
                await GetBlob(sessionId)
                    .UploadAsync(content, uploadOptions, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (RequestFailedException exception) when (exception.Status is 409 or 412)
            {
                if (attempt == maximumWriteAttempts)
                {
                    throw new IOException(
                        $"SessionFS state for '{sessionId}' could not be updated after "
                        + $"{maximumWriteAttempts} concurrency attempts.",
                        exception);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(attempt * 25), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new UnreachableException();
    }

    public async Task DeleteAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        await GetBlob(sessionId)
            .DeleteIfExistsAsync(
                DeleteSnapshotsOption.IncludeSnapshots,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    private async Task<VersionedState> ReadVersionedAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            BlobDownloadResult result =
                await GetBlob(sessionId).DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            AzureSessionFsState? state =
                result.Content.ToObjectFromJson<AzureSessionFsState>(JsonOptions);
            return new VersionedState(
                state ?? throw new InvalidDataException(
                    $"SessionFS state for '{sessionId}' is empty."),
                result.Details.ETag);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return new VersionedState(new AzureSessionFsState(), null);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"SessionFS state for '{sessionId}' is not valid JSON.",
                exception);
        }
    }

    private BlobClient GetBlob(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return container.GetBlobClient($"sessions/{Uri.EscapeDataString(sessionId)}/state.json");
    }

    private sealed record VersionedState(AzureSessionFsState State, ETag? ETag);
}

public sealed class AzureSessionFsState
{
    public long Version { get; set; }

    public HashSet<Guid> AppliedMutationIds { get; init; } = [];

    public Dictionary<string, AzureSessionFsNode> Nodes { get; init; } =
        new(StringComparer.Ordinal);
}

public sealed class AzureSessionFsNode
{
    public required string Kind { get; init; }

    public string? Content { get; set; }

    public int? Mode { get; set; }

    public DateTimeOffset Birthtime { get; init; }

    public DateTimeOffset Mtime { get; set; }

    public long Version { get; set; }
}
