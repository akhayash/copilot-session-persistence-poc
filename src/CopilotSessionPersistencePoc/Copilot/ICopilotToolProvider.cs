using Microsoft.Extensions.AI;

namespace CopilotSessionPersistencePoc.Copilot;

public interface ICopilotToolProvider
{
    IReadOnlyList<AIFunction> CreateTools(string sessionId);
}
