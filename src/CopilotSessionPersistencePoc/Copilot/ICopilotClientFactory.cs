using GitHub.Copilot;

namespace CopilotSessionPersistencePoc.Copilot;

public interface ICopilotClientFactory
{
    Task<CopilotClient> GetClientAsync(CancellationToken cancellationToken = default);
}
