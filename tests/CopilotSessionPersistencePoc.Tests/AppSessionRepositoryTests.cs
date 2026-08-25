using CopilotSessionPersistencePoc.AppState;
using CopilotSessionPersistencePoc.Persistence;

namespace CopilotSessionPersistencePoc.Tests;

public sealed class AppSessionRepositoryTests : IDisposable
{
    private readonly TestSqliteConnectionFactory connectionFactory = new();

    [Fact]
    public async Task SessionMetadataSurvivesRepositoryInstances()
    {
        await new DatabaseInitializer(connectionFactory).InitializeAsync();
        var firstRepository = new SqliteAppSessionRepository(connectionFactory);
        var created = await firstRepository.CreateAsync("session-1", "First", "gpt-5.4");

        var initialized = await firstRepository.MarkInitializedAsync(
            created.Id,
            created.Version);
        var secondRepository = new SqliteAppSessionRepository(connectionFactory);
        var restored = await secondRepository.GetAsync(created.Id);

        Assert.NotNull(restored);
        Assert.True(restored.IsInitialized);
        Assert.Equal(initialized.Version, restored.Version);
    }

    [Fact]
    public async Task VersionedUpdateRejectsLostUpdate()
    {
        await new DatabaseInitializer(connectionFactory).InitializeAsync();
        var repository = new SqliteAppSessionRepository(connectionFactory);
        var created = await repository.CreateAsync("session-1", "First", "gpt-5.4");
        await repository.TouchAsync(created.Id, created.Version);

        await Assert.ThrowsAsync<SessionConcurrencyException>(
            () => repository.TouchAsync(created.Id, created.Version));
    }

    public void Dispose() => connectionFactory.Dispose();
}
