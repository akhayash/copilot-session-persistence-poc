namespace CopilotSessionPersistencePoc.AppState;

public interface IAppSessionRepository
{
    Task<IReadOnlyList<AppSession>> ListAsync(CancellationToken cancellationToken = default);

    Task<AppSession?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<bool> ExistsForDeletionAsync(
        string id,
        CancellationToken cancellationToken = default);

    Task<AppSession> CreateAsync(
        string id,
        string title,
        string model,
        CancellationToken cancellationToken = default);

    Task<AppSession> MarkInitializedAsync(
        string id,
        long expectedVersion,
        string? title = null,
        CancellationToken cancellationToken = default);

    Task TouchAsync(
        string id,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}
