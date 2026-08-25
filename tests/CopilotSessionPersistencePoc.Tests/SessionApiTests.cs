using System.Net;
using System.Net.Http.Json;
using CopilotSessionPersistencePoc.Api;
using CopilotSessionPersistencePoc.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace CopilotSessionPersistencePoc.Tests;

public sealed class SessionApiTests
{
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
                Assert.Empty(diagnostics.Entries);

                HttpResponseMessage deleteResponse = await client.DeleteAsync($"/api/sessions/{created.Id}");
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
                Assert.Equal(
                    HttpStatusCode.NotFound,
                    (await client.GetAsync($"/api/sessions/{created.Id}")).StatusCode);
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
}
