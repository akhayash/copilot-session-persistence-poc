using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Data.Tables;
using CopilotSessionPersistencePoc.AppState;
using CopilotSessionPersistencePoc.Persistence;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Execution;

public sealed class AzureTableExecutionJobRepository(
    AzureStorageClients clients,
    ISessionOwnerContext ownerContext,
    IOptions<AzureStorageOptions> options)
    : IExecutionJobRepository
{
    private readonly TableClient table =
        clients.TableService.GetTableClient(options.Value.ExecutionJobsTable);

    public async Task<ExecutionJob?> GetAsync(
        string toolCallId,
        CancellationToken cancellationToken)
    {
        NullableResponse<ExecutionJobEntity> response =
            await table.GetEntityIfExistsAsync<ExecutionJobEntity>(
                ownerContext.OwnerKey,
                CreateStorageKey(toolCallId),
                cancellationToken: cancellationToken);
        return response.HasValue ? ToModel(response.Value!) : null;
    }

    public async Task<ExecutionJob?> GetByJobIdAsync(
        string sessionId,
        string jobId,
        CancellationToken cancellationToken)
    {
        ExecutionJob? match = null;
        await foreach (ExecutionJobEntity entity in table.QueryAsync<ExecutionJobEntity>(
            entity => entity.PartitionKey == ownerContext.OwnerKey
                && entity.SessionId == sessionId
                && entity.JobId == jobId,
            maxPerPage: 2,
            cancellationToken: cancellationToken))
        {
            if (match is not null)
            {
                throw new IOException(
                    $"Multiple execution jobs use job ID '{jobId}'.");
            }

            match = ToModel(entity);
        }

        return match;
    }

    public async Task<ExecutionJobReservation> GetOrCreateAsync(
        string sessionId,
        string toolCallId,
        string codeSha256,
        CancellationToken cancellationToken)
    {
        string storageKey = CreateStorageKey(toolCallId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var entity = new ExecutionJobEntity
        {
            PartitionKey = ownerContext.OwnerKey,
            RowKey = storageKey,
            JobId = $"job-{Guid.NewGuid():N}",
            SessionId = sessionId,
            ToolCallId = toolCallId,
            CodeSha256 = codeSha256,
            Status = ExecutionJobStatus.Pending.ToString(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        bool created;
        try
        {
            await table.AddEntityAsync(entity, cancellationToken);
            created = true;
        }
        catch (RequestFailedException exception) when (exception.Status == 409)
        {
            created = false;
        }

        ExecutionJob current = await GetRequiredAsync(storageKey, cancellationToken);
        if (!string.Equals(current.SessionId, sessionId, StringComparison.Ordinal)
            || !string.Equals(current.ToolCallId, toolCallId, StringComparison.Ordinal)
            || !string.Equals(current.CodeSha256, codeSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A conflicting execution job already exists for this tool invocation.");
        }

        return new ExecutionJobReservation(current, created);
    }

    public async Task<ExecutionJob> UpdateAsync(
        ExecutionJob job,
        ExecutionJobStatus status,
        string? standardOutput,
        string? standardError,
        string? failureMessage,
        string? outputsJson,
        CancellationToken cancellationToken)
    {
        var entity = ToEntity(job, ownerContext.OwnerKey);
        entity.Status = status.ToString();
        entity.StandardOutput = standardOutput;
        entity.StandardError = standardError;
        entity.Error = failureMessage;
        entity.OutputsJson = outputsJson;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await table.UpdateEntityAsync(
                entity,
                job.ETag,
                TableUpdateMode.Replace,
                cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == 412)
        {
            throw new ExecutionJobConcurrencyException(
                $"Execution job '{job.JobId}' was modified concurrently.",
                exception);
        }

        return await GetRequiredAsync(job.StorageKey, cancellationToken);
    }

    public async Task DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var entities = new List<ExecutionJobEntity>();
        await foreach (ExecutionJobEntity entity in table.QueryAsync<ExecutionJobEntity>(
            entity => entity.PartitionKey == ownerContext.OwnerKey
                && entity.SessionId == sessionId,
            cancellationToken: cancellationToken))
        {
            entities.Add(entity);
        }

        foreach (ExecutionJobEntity entity in entities)
        {
            await table.DeleteEntityAsync(
                entity.PartitionKey,
                entity.RowKey,
                ETag.All,
                cancellationToken);
        }
    }

    private async Task<ExecutionJob> GetRequiredAsync(
        string storageKey,
        CancellationToken cancellationToken)
    {
        NullableResponse<ExecutionJobEntity> response =
            await table.GetEntityIfExistsAsync<ExecutionJobEntity>(
                ownerContext.OwnerKey,
                storageKey,
                cancellationToken: cancellationToken);
        return response.HasValue
            ? ToModel(response.Value!)
            : throw new IOException(
                $"Execution job state '{storageKey}' was not found.");
    }

    private static string CreateStorageKey(string toolCallId) =>
        $"job-{Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(toolCallId)))}";

    private static ExecutionJob ToModel(ExecutionJobEntity entity) => new(
        entity.RowKey,
        entity.JobId,
        entity.SessionId,
        entity.ToolCallId,
        entity.CodeSha256,
        Enum.Parse<ExecutionJobStatus>(entity.Status, ignoreCase: false),
        entity.StandardOutput,
        entity.StandardError,
        entity.Error,
        entity.OutputsJson,
        entity.CreatedAt,
        entity.UpdatedAt,
        entity.ETag);

    private static ExecutionJobEntity ToEntity(ExecutionJob job, string ownerKey) => new()
    {
        PartitionKey = ownerKey,
        RowKey = job.StorageKey,
        JobId = job.JobId,
        SessionId = job.SessionId,
        ToolCallId = job.ToolCallId,
        CodeSha256 = job.CodeSha256,
        Status = job.Status.ToString(),
        StandardOutput = job.StandardOutput,
        StandardError = job.StandardError,
        Error = job.Error,
        OutputsJson = job.OutputsJson,
        CreatedAt = job.CreatedAt,
        UpdatedAt = job.UpdatedAt,
        ETag = job.ETag,
    };

    public sealed class ExecutionJobEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = string.Empty;

        public string RowKey { get; set; } = string.Empty;

        public DateTimeOffset? Timestamp { get; set; }

        public ETag ETag { get; set; }

        public string JobId { get; set; } = string.Empty;

        public string SessionId { get; set; } = string.Empty;

        public string ToolCallId { get; set; } = string.Empty;

        public string CodeSha256 { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? StandardOutput { get; set; }

        public string? StandardError { get; set; }

        public string? Error { get; set; }

        public string? OutputsJson { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }
}
