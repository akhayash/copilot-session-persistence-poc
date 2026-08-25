using CopilotSessionPersistencePoc.AppState;
using CopilotSessionPersistencePoc.Copilot;

namespace CopilotSessionPersistencePoc.Api;

public sealed record CreateSessionRequest(string? Title, string? Model);

public sealed record SendMessageRequest(string Prompt);

public sealed record SendMessageResponse(CopilotMessage Message);

public sealed record SessionResponse(
    string Id,
    string Title,
    string Model,
    bool IsInitialized,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version)
{
    public static SessionResponse From(AppSession session) => new(
        session.Id,
        session.Title,
        session.Model,
        session.IsInitialized,
        session.CreatedAt,
        session.UpdatedAt,
        session.Version);
}

public sealed record SessionDetailsResponse(SessionResponse Session, IReadOnlyList<CopilotMessage> Messages);

public sealed record HealthResponse(string Status, string Persistence, string CopilotCli);
