namespace CopilotSessionPersistencePoc.Persistence;

public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";

    public string Backend { get; init; } = "Sqlite";

    public string DatabasePath { get; init; } = "data/session-state.db";

    public int BusyTimeoutMilliseconds { get; init; } = 5000;
}
