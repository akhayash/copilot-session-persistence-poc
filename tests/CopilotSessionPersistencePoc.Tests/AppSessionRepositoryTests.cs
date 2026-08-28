using CopilotSessionPersistencePoc.AppState;
using CopilotSessionPersistencePoc.Persistence;

namespace CopilotSessionPersistencePoc.Tests;

public sealed class AppSessionRepositoryTests : IDisposable
{
    private readonly TestSqliteConnectionFactory connectionFactory = new();
    private readonly TestSessionOwnerContext owner = new("test-user");

    [Fact]
    public async Task SessionMetadataSurvivesRepositoryInstances()
    {
        await new DatabaseInitializer(connectionFactory).InitializeAsync();
        var firstRepository = new SqliteAppSessionRepository(connectionFactory, owner);
        var created = await firstRepository.CreateAsync("session-1", "First", "gpt-5.4");

        var initialized = await firstRepository.MarkInitializedAsync(
            created.Id,
            created.Version,
            "First prompt title");
        var secondRepository = new SqliteAppSessionRepository(connectionFactory, owner);
        var restored = await secondRepository.GetAsync(created.Id);

        Assert.NotNull(restored);
        Assert.True(restored.IsInitialized);
        Assert.Equal("First prompt title", restored.Title);
        Assert.Equal(initialized.Version, restored.Version);
    }

    [Fact]
    public async Task VersionedUpdateRejectsLostUpdate()
    {
        await new DatabaseInitializer(connectionFactory).InitializeAsync();
        var repository = new SqliteAppSessionRepository(connectionFactory, owner);
        var created = await repository.CreateAsync("session-1", "First", "gpt-5.4");
        await repository.TouchAsync(created.Id, created.Version);

        await Assert.ThrowsAsync<SessionConcurrencyException>(
            () => repository.TouchAsync(created.Id, created.Version));
    }

    [Fact]
    public async Task DifferentOwnersCannotReadUpdateOrDeleteEachOthersSessions()
    {
        await new DatabaseInitializer(connectionFactory).InitializeAsync();
        var ownerA = new SqliteAppSessionRepository(
            connectionFactory,
            new TestSessionOwnerContext("user-a"));
        var ownerB = new SqliteAppSessionRepository(
            connectionFactory,
            new TestSessionOwnerContext("user-b"));
        AppSession created = await ownerA.CreateAsync(
            "session-owned-by-a",
            "Private",
            "gpt-5.4");

        Assert.Empty(await ownerB.ListAsync());
        Assert.Null(await ownerB.GetAsync(created.Id));
        Assert.False(await ownerB.ExistsForDeletionAsync(created.Id));
        await Assert.ThrowsAsync<SessionConcurrencyException>(
            () => ownerB.TouchAsync(created.Id, created.Version));
        await ownerB.DeleteAsync(created.Id);

        Assert.NotNull(await ownerA.GetAsync(created.Id));
    }

    [Fact]
    public async Task ExistingSqliteSessionsAreAssignedToConfiguredLocalOwner()
    {
        await using (var connection = await connectionFactory.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_info (version INTEGER NOT NULL);
                INSERT INTO schema_info (version) VALUES (1);
                CREATE TABLE app_sessions (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    model TEXT NOT NULL,
                    is_initialized INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    version INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO app_sessions
                    (id, title, model, created_at, updated_at)
                VALUES
                    ('legacy-session', 'Legacy', 'gpt-5.4',
                     '2026-08-28T00:00:00+00:00', '2026-08-28T00:00:00+00:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await new DatabaseInitializer(connectionFactory).InitializeAsync();
        var localRepository = new SqliteAppSessionRepository(
            connectionFactory,
            new TestSessionOwnerContext("local-user"));
        var otherRepository = new SqliteAppSessionRepository(
            connectionFactory,
            new TestSessionOwnerContext("other-user"));

        Assert.NotNull(await localRepository.GetAsync("legacy-session"));
        Assert.Null(await otherRepository.GetAsync("legacy-session"));
    }

    [Fact]
    public async Task IncompleteOwnerMigrationBackfillsEmptyOwnerRows()
    {
        await using (var connection = await connectionFactory.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_info (version INTEGER NOT NULL);
                INSERT INTO schema_info (version) VALUES (1);
                CREATE TABLE app_sessions (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    model TEXT NOT NULL,
                    is_initialized INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    version INTEGER NOT NULL DEFAULT 0,
                    owner_id TEXT NOT NULL DEFAULT ''
                );
                INSERT INTO app_sessions
                    (id, title, model, created_at, updated_at)
                VALUES
                    ('interrupted-session', 'Interrupted', 'gpt-5.4',
                     '2026-08-28T00:00:00+00:00', '2026-08-28T00:00:00+00:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await new DatabaseInitializer(connectionFactory).InitializeAsync();
        var localRepository = new SqliteAppSessionRepository(
            connectionFactory,
            new TestSessionOwnerContext("local-user"));

        Assert.NotNull(await localRepository.GetAsync("interrupted-session"));
    }

    public void Dispose() => connectionFactory.Dispose();
}
