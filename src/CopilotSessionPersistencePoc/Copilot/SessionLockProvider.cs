using System.Collections.Concurrent;

namespace CopilotSessionPersistencePoc.Copilot;

public sealed class SessionLockProvider : ISessionLockProvider
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<ISessionLockHandle> TryAcquireAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim sessionLock = _locks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        if (!await sessionLock.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            throw new SessionBusyException(sessionId);
        }

        return new Releaser(_locks, sessionId, sessionLock);
    }

    private sealed class Releaser(
        ConcurrentDictionary<string, SemaphoreSlim> locks,
        string sessionId,
        SemaphoreSlim sessionLock)
        : ISessionLockHandle
    {
        private bool deleteOnRelease;
        private bool _disposed;

        public CancellationToken LockLost => CancellationToken.None;

        public void DeleteOnRelease() => deleteOnRelease = true;

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                sessionLock.Release();
                if (deleteOnRelease
                    && locks.TryGetValue(sessionId, out SemaphoreSlim? current)
                    && ReferenceEquals(current, sessionLock))
                {
                    locks.TryRemove(sessionId, out _);
                }

                _disposed = true;
            }

            return ValueTask.CompletedTask;
        }
    }
}
