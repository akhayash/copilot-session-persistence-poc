using GitHub.Copilot;

namespace CopilotSessionPersistencePoc.SessionFs;

public interface ISessionFsProviderFactory
{
    SessionFsProvider Create(string sessionId);

    Task<bool> HasSessionStateAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}
