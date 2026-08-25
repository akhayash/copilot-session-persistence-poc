namespace CopilotSessionPersistencePoc.AppState;

public sealed class SessionConcurrencyException(string sessionId)
    : Exception($"Session '{sessionId}' was modified by another request.");
