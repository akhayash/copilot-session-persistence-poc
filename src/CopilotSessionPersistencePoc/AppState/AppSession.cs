namespace CopilotSessionPersistencePoc.AppState;

public sealed record AppSession(
    string Id,
    string Title,
    string Model,
    bool IsInitialized,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version);
