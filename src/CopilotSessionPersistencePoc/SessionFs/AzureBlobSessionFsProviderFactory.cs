using GitHub.Copilot;

namespace CopilotSessionPersistencePoc.SessionFs;

public sealed class AzureBlobSessionFsProviderFactory(AzureBlobSessionFsStore store)
    : ISessionFsProviderFactory
{
    public SessionFsProvider Create(string sessionId) =>
        new AzureBlobSessionFsProvider(store, sessionId);

    public Task<bool> HasSessionStateAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        store.ExistsAsync(sessionId, cancellationToken);
}
