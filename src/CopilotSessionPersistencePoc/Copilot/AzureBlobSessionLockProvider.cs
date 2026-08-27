using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using CopilotSessionPersistencePoc.Persistence;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Copilot;

public sealed class AzureBlobSessionLockProvider(
    AzureStorageClients clients,
    IOptions<AzureStorageOptions> options,
    ILogger<AzureBlobSessionLockProvider> logger)
    : ISessionLockProvider
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    private static readonly Action<ILogger, string, Exception?> LogLeaseReleaseFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogLeaseReleaseFailure)),
            "Session lease {SessionId} was no longer owned when release was attempted.");
    private static readonly Action<ILogger, string, Exception?> LogLeaseRenewalFailure =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2, nameof(LogLeaseRenewalFailure)),
            "Distributed session lease {SessionId} could not be renewed.");
    private static readonly Action<ILogger, string, Exception?> LogLockBlobCleanupFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(3, nameof(LogLockBlobCleanupFailure)),
            "Released session lock blob {SessionId} could not be deleted.");
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RenewalDeadlineMargin = TimeSpan.FromSeconds(5);
    private readonly BlobContainerClient container =
        clients.BlobService.GetBlobContainerClient(options.Value.SessionLocksContainer);
    private readonly int maximumWriteAttempts = options.Value.MaximumWriteAttempts;

    public async Task<ISessionLockHandle> TryAcquireAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        BlobClient blob =
            container.GetBlobClient($"sessions/{Uri.EscapeDataString(sessionId)}.lock");
        for (var attempt = 1; attempt <= maximumWriteAttempts; attempt++)
        {
            try
            {
                await blob.UploadAsync(
                        BinaryData.FromString(string.Empty),
                        new BlobUploadOptions
                        {
                            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (RequestFailedException exception) when (exception.Status is 409 or 412)
            {
                // The lock blob already exists; acquiring its lease is the actual lock operation.
            }

            BlobLeaseClient lease = blob.GetBlobLeaseClient();
            try
            {
                await lease.AcquireAsync(LeaseDuration, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return new LeaseHandle(blob, lease, sessionId, logger);
            }
            catch (RequestFailedException exception) when (exception.Status == 409)
            {
                throw new SessionBusyException(sessionId);
            }
            catch (RequestFailedException exception) when (exception.Status == 404)
            {
                if (attempt == maximumWriteAttempts)
                {
                    throw new IOException(
                        $"Session lock for '{sessionId}' could not be acquired because "
                        + "its blob changed repeatedly.",
                        exception);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(attempt * 25), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new IOException(
            $"Session lock for '{sessionId}' could not be acquired because its blob changed.");
    }

    private sealed class LeaseHandle : ISessionLockHandle
    {
        private readonly BlobClient blob;
        private readonly CancellationTokenSource stop = new();
        private readonly CancellationTokenSource lockLost = new();
        private readonly BlobLeaseClient lease;
        private readonly string sessionId;
        private readonly ILogger logger;
        private readonly Task renewal;
        private bool deleteOnRelease;
        private bool disposed;

        public LeaseHandle(
            BlobClient blob,
            BlobLeaseClient lease,
            string sessionId,
            ILogger logger)
        {
            this.blob = blob;
            this.lease = lease;
            this.sessionId = sessionId;
            this.logger = logger;
            renewal = RenewAsync(lease, sessionId, logger, lockLost, stop.Token);
        }

        public CancellationToken LockLost => lockLost.Token;

        public void DeleteOnRelease() => deleteOnRelease = true;

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await renewal.ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException
                || exception is RequestFailedException)
            {
                if (exception is not OperationCanceledException
                    || !stop.IsCancellationRequested)
                {
                    LogLeaseRenewalFailure(logger, sessionId, exception);
                }
            }

            bool released = false;
            try
            {
                await lease.ReleaseAsync().ConfigureAwait(false);
                released = true;
            }
            catch (Exception exception)
            {
                LogLeaseReleaseFailure(logger, sessionId, exception);
            }

            if (released && deleteOnRelease)
            {
                try
                {
                    await blob.DeleteIfExistsAsync(
                            DeleteSnapshotsOption.IncludeSnapshots)
                        .ConfigureAwait(false);
                }
                catch (RequestFailedException exception) when (exception.Status is 409 or 412)
                {
                }
                catch (Exception exception)
                {
                    LogLockBlobCleanupFailure(logger, sessionId, exception);
                }
            }

            stop.Dispose();
            lockLost.Dispose();
        }

        private static async Task RenewAsync(
            BlobLeaseClient lease,
            string sessionId,
            ILogger logger,
            CancellationTokenSource lockLost,
            CancellationToken cancellationToken)
        {
            DateTimeOffset confirmedExpiry = DateTimeOffset.UtcNow + LeaseDuration;
            using var timer = new PeriodicTimer(RenewalInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    TimeSpan remaining =
                        confirmedExpiry - DateTimeOffset.UtcNow - RenewalDeadlineMargin;
                    if (remaining <= TimeSpan.Zero)
                    {
                        await lockLost.CancelAsync().ConfigureAwait(false);
                        return;
                    }

                    using var attemptCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    Task renewalAttempt =
                        lease.RenewAsync(cancellationToken: attemptCancellation.Token);
                    Task deadline = Task.Delay(remaining, cancellationToken);
                    Task completed =
                        await Task.WhenAny(renewalAttempt, deadline).ConfigureAwait(false);
                    if (completed != renewalAttempt)
                    {
                        await attemptCancellation.CancelAsync().ConfigureAwait(false);
                        await lockLost.CancelAsync().ConfigureAwait(false);
                        _ = ObserveFailureAsync(
                            renewalAttempt,
                            logger,
                            sessionId);
                        return;
                    }

                    await renewalAttempt.ConfigureAwait(false);
                    confirmedExpiry = DateTimeOffset.UtcNow + LeaseDuration;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                LogLeaseRenewalFailure(logger, sessionId, exception);
                await lockLost.CancelAsync().ConfigureAwait(false);
            }
        }

        private static async Task ObserveFailureAsync(
            Task task,
            ILogger logger,
            string sessionId)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                LogLeaseRenewalFailure(logger, sessionId, exception);
            }
        }
    }
}
