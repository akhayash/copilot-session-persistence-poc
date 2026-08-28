using System.Net;
using System.Net.Http.Json;
using CopilotSessionPersistencePoc.Api;
using CopilotSessionPersistencePoc.AppState;
using CopilotSessionPersistencePoc.Copilot;
using CopilotSessionPersistencePoc.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CopilotSessionPersistencePoc.Tests;

public sealed class SessionApiTests
{
    [Fact]
    public async Task AuthenticatedUsersOnlySeeTheirOwnSessions()
    {
        string directory = Path.Join(Path.GetTempPath(), $"copilot-api-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        WebApplicationFactory<Program>? factory = null;

        try
        {
            factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, configuration) =>
                    {
                        configuration.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Persistence:DatabasePath"] = Path.Join(directory, "sessions.db"),
                            ["SessionOwnership:RequireAuthenticatedPrincipal"] = "true",
                        });
                    });
                });
            using HttpClient anonymous = factory.CreateClient();
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await anonymous.GetAsync("/api/sessions")).StatusCode);

            using HttpClient userA = factory.CreateClient();
            userA.DefaultRequestHeaders.Add(
                HttpSessionOwnerContext.PrincipalIdHeader,
                "entra-user-a");
            using HttpClient userB = factory.CreateClient();
            userB.DefaultRequestHeaders.Add(
                HttpSessionOwnerContext.PrincipalIdHeader,
                "entra-user-b");

            HttpResponseMessage createResponse = await userA.PostAsJsonAsync(
                "/api/sessions",
                new CreateSessionRequest("User A session", "gpt-5-mini"));
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
            SessionResponse created =
                Assert.IsType<SessionResponse>(
                    await createResponse.Content.ReadFromJsonAsync<SessionResponse>());

            Assert.Single(
                Assert.IsType<SessionResponse[]>(
                    await userA.GetFromJsonAsync<SessionResponse[]>("/api/sessions")));
            Assert.Empty(
                Assert.IsType<SessionResponse[]>(
                    await userB.GetFromJsonAsync<SessionResponse[]>("/api/sessions")));
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await userB.GetAsync($"/api/sessions/{created.Id}")).StatusCode);
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await userB.GetAsync(
                    $"/api/sessions/{created.Id}/diagnostics")).StatusCode);
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await userB.DeleteAsync($"/api/sessions/{created.Id}")).StatusCode);
            Assert.Equal(
                HttpStatusCode.OK,
                (await userA.GetAsync($"/api/sessions/{created.Id}")).StatusCode);
        }
        finally
        {
            if (factory is not null)
            {
                await factory.DisposeAsync();
            }

            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GetReturnsNotFoundWhenSessionIsDeletedBeforeLockAcquisition()
    {
        string directory = Path.Join(Path.GetTempPath(), $"copilot-api-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        WebApplicationFactory<Program>? factory = null;

        try
        {
            factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, configuration) =>
                    {
                        configuration.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Persistence:DatabasePath"] = Path.Join(directory, "sessions.db"),
                        });
                    });
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IAppSessionRepository>();
                        services.AddSingleton<IAppSessionRepository, ConcurrentDeleteRepository>();
                    });
                });

            using HttpClient client = factory.CreateClient();
            HttpResponseMessage response = await client.GetAsync("/api/sessions/session-id");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            if (factory is not null)
            {
                await factory.DisposeAsync();
            }

            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteReturnsServiceUnavailableWhenDistributedLockIsLost()
    {
        string directory = Path.Join(Path.GetTempPath(), $"copilot-api-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        WebApplicationFactory<Program>? factory = null;

        try
        {
            factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, configuration) =>
                    {
                        configuration.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Persistence:DatabasePath"] = Path.Join(directory, "sessions.db"),
                        });
                    });
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IAppSessionRepository>();
                        services.RemoveAll<ISessionLockProvider>();
                        services.AddSingleton<IAppSessionRepository, CancelledDeleteRepository>();
                        services.AddSingleton<ISessionLockProvider, LostSessionLockProvider>();
                    });
                });

            using HttpClient client = factory.CreateClient();
            HttpResponseMessage response = await client.DeleteAsync("/api/sessions/session-id");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync();
            Assert.Contains("distributed session lock was lost", body, StringComparison.Ordinal);
        }
        finally
        {
            if (factory is not null)
            {
                await factory.DisposeAsync();
            }

            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CrudPersistsSessionMetadataWithoutStartingCopilot()
    {
        string directory = Path.Join(Path.GetTempPath(), $"copilot-api-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string databasePath = Path.Join(directory, "sessions.db");
        WebApplicationFactory<Program>? factory = null;

        try
        {
            factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, configuration) =>
                    {
                        configuration.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Persistence:DatabasePath"] = databasePath,
                        });
                    });
                });
            using (HttpClient client = factory.CreateClient())
            {
                HttpResponseMessage createResponse = await client.PostAsJsonAsync(
                    "/api/sessions",
                    new CreateSessionRequest("API test", "gpt-5-mini"));
                Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
                SessionResponse created =
                    Assert.IsType<SessionResponse>(await createResponse.Content.ReadFromJsonAsync<SessionResponse>());
                Assert.False(created.IsInitialized);

                SessionResponse[] sessions =
                    Assert.IsType<SessionResponse[]>(
                        await client.GetFromJsonAsync<SessionResponse[]>("/api/sessions"));
                Assert.Contains(sessions, session => session.Id == created.Id);

                SessionDetailsResponse details = Assert.IsType<SessionDetailsResponse>(
                    await client.GetFromJsonAsync<SessionDetailsResponse>($"/api/sessions/{created.Id}"));
                Assert.Empty(details.Messages);

                SessionFsDiagnosticsSnapshot diagnostics =
                    Assert.IsType<SessionFsDiagnosticsSnapshot>(
                        await client.GetFromJsonAsync<SessionFsDiagnosticsSnapshot>(
                            $"/api/sessions/{created.Id}/diagnostics"));
                Assert.Equal("SQLite custom SessionFS provider", diagnostics.Storage.Backend);
                Assert.Equal("Sqlite", diagnostics.Storage.PersistenceMode);
                Assert.Equal(
                    "SQLite table: app_sessions",
                    diagnostics.Storage.ApplicationMetadataBackend);
                Assert.Equal(
                    "SQLite table: session_fs_nodes",
                    diagnostics.Storage.SessionFsBackend);
                Assert.Empty(diagnostics.Entries);

                HttpResponseMessage deleteResponse = await client.DeleteAsync($"/api/sessions/{created.Id}");
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
                Assert.Equal(
                    HttpStatusCode.NotFound,
                    (await client.GetAsync($"/api/sessions/{created.Id}")).StatusCode);

                HttpResponseMessage repeatDeleteResponse =
                    await client.DeleteAsync($"/api/sessions/{created.Id}");
                Assert.Equal(HttpStatusCode.NoContent, repeatDeleteResponse.StatusCode);
            }

        }
        finally
        {
            if (factory is not null)
            {
                await factory.DisposeAsync();
            }

            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ArtifactApiExplainsThatSqliteModeIsUnavailable()
    {
        string directory = Path.Join(
            Path.GetTempPath(),
            $"copilot-artifact-api-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        WebApplicationFactory<Program>? factory = null;

        try
        {
            factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["Persistence:DatabasePath"] =
                                    Path.Join(directory, "sessions.db"),
                            });
                    });
                });
            using HttpClient client = factory.CreateClient();
            SessionResponse created = Assert.IsType<SessionResponse>(
                await (await client.PostAsJsonAsync(
                    "/api/sessions",
                    new CreateSessionRequest("Artifacts", "gpt-5-mini")))
                    .Content.ReadFromJsonAsync<SessionResponse>());

            HttpResponseMessage response = await client.GetAsync(
                $"/api/sessions/{created.Id}/artifacts");

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains(
                "AzureStorage",
                await response.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }
        finally
        {
            if (factory is not null)
            {
                await factory.DisposeAsync();
            }

            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CancelledDeleteRepository : IAppSessionRepository
    {
        public Task<IReadOnlyList<AppSession>> ListAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AppSession?> GetAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AppSession?>(
                new AppSession(
                    id,
                    "Delete test",
                    "gpt-5-mini",
                    true,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    0));

        public Task<bool> ExistsForDeletionAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<AppSession> CreateAsync(
            string id,
            string title,
            string model,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AppSession> MarkInitializedAsync(
            string id,
            long expectedVersion,
            string? title = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task TouchAsync(
            string id,
            long expectedVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task DeleteAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class ConcurrentDeleteRepository : IAppSessionRepository
    {
        private int getCount;

        public Task<IReadOnlyList<AppSession>> ListAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AppSession?> GetAsync(
            string id,
            CancellationToken cancellationToken = default)
        {
            AppSession? result = Interlocked.Increment(ref getCount) == 1
                ? new AppSession(
                    id,
                    "Concurrent delete",
                    "gpt-5-mini",
                    true,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    0)
                : null;
            return Task.FromResult(result);
        }

        public Task<bool> ExistsForDeletionAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(getCount == 0);

        public Task<AppSession> CreateAsync(
            string id,
            string title,
            string model,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AppSession> MarkInitializedAsync(
            string id,
            long expectedVersion,
            string? title = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task TouchAsync(
            string id,
            long expectedVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class LostSessionLockProvider : ISessionLockProvider
    {
        public Task<ISessionLockHandle> TryAcquireAsync(
            string sessionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<ISessionLockHandle>(new LostSessionLockHandle());
    }

    private sealed class LostSessionLockHandle : ISessionLockHandle
    {
        public CancellationToken LockLost { get; } = new(canceled: true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
