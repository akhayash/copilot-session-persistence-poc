namespace CopilotSessionPersistencePoc.Copilot;

public interface ISessionLockProvider
{
    Task<ISessionLockHandle> TryAcquireAsync(
        string sessionId,
        CancellationToken cancellationToken);
}

public interface ISessionLockHandle : IAsyncDisposable
{
    CancellationToken LockLost { get; }

    void DeleteOnRelease()
    {
    }
}
