using Azure;
using Azure.Data.Tables;
using CopilotSessionPersistencePoc.ArtifactStorage;
using CopilotSessionPersistencePoc.Persistence;
using CopilotSessionPersistencePoc.SessionFs;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.AppState;

public sealed class AzureTableAppSessionRepository(
    AzureStorageClients clients,
    AzureBlobSessionFsStore sessionFsStore,
    IArtifactStore artifactStore,
    ISessionOwnerContext ownerContext,
    IOptions<AzureStorageOptions> options)
    : IAppSessionRepository
{
    private readonly TableClient table =
        clients.TableService.GetTableClient(options.Value.AppSessionsTable);

    public async Task<IReadOnlyList<AppSession>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var sessions = new List<AppSession>();
        await foreach (AppSessionEntity entity in table.QueryAsync<AppSessionEntity>(
            entity => entity.PartitionKey == ownerContext.OwnerKey,
            cancellationToken: cancellationToken))
        {
            if (!entity.IsDeleting)
            {
                sessions.Add(ToModel(entity));
            }
        }

        return sessions
            .OrderByDescending(static session => session.UpdatedAt)
            .ToArray();
    }

    public async Task<AppSession?> GetAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            NullableResponse<AppSessionEntity> response =
                await table.GetEntityIfExistsAsync<AppSessionEntity>(
                    ownerContext.OwnerKey,
                    id,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return response is { HasValue: true, Value: { IsDeleting: false } }
                ? ToModel(response.Value)
                : null;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task<bool> ExistsForDeletionAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            NullableResponse<AppSessionEntity> response =
                await table.GetEntityIfExistsAsync<AppSessionEntity>(
                    ownerContext.OwnerKey,
                    id,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return response.HasValue;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return false;
        }
    }

    public async Task<AppSession> CreateAsync(
        string id,
        string title,
        string model,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var entity = new AppSessionEntity
        {
            PartitionKey = ownerContext.OwnerKey,
            RowKey = id,
            Title = title,
            Model = model,
            IsInitialized = false,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 0,
        };
        await table.AddEntityAsync(entity, cancellationToken).ConfigureAwait(false);
        return ToModel(entity);
    }

    public Task<AppSession> MarkInitializedAsync(
        string id,
        long expectedVersion,
        string? title = null,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(id, expectedVersion, markInitialized: true, title, cancellationToken);

    public async Task TouchAsync(
        string id,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        _ = await UpdateAsync(
                id,
                expectedVersion,
                markInitialized: false,
                title: null,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task DeleteAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        NullableResponse<AppSessionEntity> response =
            await table.GetEntityIfExistsAsync<AppSessionEntity>(
                ownerContext.OwnerKey,
                id,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!response.HasValue)
        {
            return;
        }

        AppSessionEntity entity = response.Value!;
        if (!entity.IsDeleting)
        {
            entity.IsDeleting = true;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
            entity.Version++;
            await table.UpdateEntityAsync(
                    entity,
                    entity.ETag,
                    TableUpdateMode.Replace,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await sessionFsStore.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        await artifactStore.DeleteSessionAsync(id, cancellationToken).ConfigureAwait(false);
        await table.DeleteEntityAsync(ownerContext.OwnerKey, id, ETag.All, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AppSession> UpdateAsync(
        string id,
        long expectedVersion,
        bool markInitialized,
        string? title,
        CancellationToken cancellationToken)
    {
        NullableResponse<AppSessionEntity> response =
            await table.GetEntityIfExistsAsync<AppSessionEntity>(
                ownerContext.OwnerKey,
                id,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!response.HasValue)
        {
            throw new KeyNotFoundException($"Session '{id}' no longer exists.");
        }

        AppSessionEntity entity = response.Value!;
        if (entity.IsDeleting || entity.Version != expectedVersion)
        {
            throw new SessionConcurrencyException(id);
        }

        entity.IsInitialized |= markInitialized;
        if (title is not null)
        {
            entity.Title = title;
        }

        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.Version++;
        try
        {
            await table.UpdateEntityAsync(
                    entity,
                    entity.ETag,
                    TableUpdateMode.Replace,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status == 412)
        {
            throw new SessionConcurrencyException(id);
        }

        return ToModel(entity);
    }

    private static AppSession ToModel(AppSessionEntity entity) =>
        new(
            entity.RowKey,
            entity.Title,
            entity.Model,
            entity.IsInitialized,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.Version);

    public sealed class AppSessionEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = string.Empty;

        public string RowKey { get; set; } = string.Empty;

        public DateTimeOffset? Timestamp { get; set; }

        public ETag ETag { get; set; }

        public string Title { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        public bool IsInitialized { get; set; }

        public bool IsDeleting { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public long Version { get; set; }
    }
}
