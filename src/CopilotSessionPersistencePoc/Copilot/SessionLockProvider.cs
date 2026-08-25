using System.Collections.Concurrent;

namespace CopilotSessionPersistencePoc.Copilot;

public sealed class SessionLockProvider
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<IDisposable> TryAcquireAsync(string sessionId, CancellationToken cancellationToken)
    {
        SemaphoreSlim sessionLock = _locks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        if (!await sessionLock.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            throw new SessionBusyException(sessionId);
        }

        return new Releaser(sessionLock);
    }

    private sealed class Releaser(SemaphoreSlim sessionLock) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                sessionLock.Release();
                _disposed = true;
            }
        }
    }
}
