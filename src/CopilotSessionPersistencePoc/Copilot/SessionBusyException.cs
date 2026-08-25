namespace CopilotSessionPersistencePoc.Copilot;

public sealed class SessionBusyException(string sessionId)
    : Exception($"Session '{sessionId}' is already processing a message.");
