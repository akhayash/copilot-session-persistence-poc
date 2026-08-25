using CopilotSessionPersistencePoc.Diagnostics;
using CopilotSessionPersistencePoc.Persistence;
using CopilotSessionPersistencePoc.SessionFs;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Tests;

public sealed class SqliteSessionFsDiagnosticsReaderTests : IDisposable
{
    private readonly TestSqliteConnectionFactory connectionFactory = new();

    [Fact]
    public async Task SnapshotShowsSQLiteRowsWithoutMatchingHostFiles()
    {
        await new DatabaseInitializer(connectionFactory).InitializeAsync();
        string sessionId = $"diagnostics-{Guid.NewGuid():N}";
        var provider = new SqliteSessionFsProvider(connectionFactory, sessionId);
        ISessionFsHandler handler = provider;
        Assert.Null(await handler.WriteFileAsync(new SessionFsWriteFileRequest
        {
            Path = "/session-state/events.jsonl",
            Content = "{\"type\":\"user.message\"}\n{\"type\":\"assistant.message\"}\n",
        }, default));
        Assert.Null(await handler.WriteFileAsync(new SessionFsWriteFileRequest
        {
            Path = "/session-state/workspace.yaml",
            Content = "token: ghp_1234567890abcdefghijklmnop",
        }, default));

        var reader = new SqliteSessionFsDiagnosticsReader(
            connectionFactory,
            Options.Create(new DiagnosticsOptions { MaximumPreviewCharacters = 24 }));
        SessionFsDiagnosticsSnapshot snapshot = await reader.GetSnapshotAsync(sessionId);
        SessionFsEntryDetails? details =
            await reader.GetEntryAsync(sessionId, "/session-state/workspace.yaml");

        Assert.Equal("SQLite custom SessionFS provider", snapshot.Storage.Backend);
        Assert.True(snapshot.Storage.DatabaseFileExists);
        Assert.False(snapshot.Storage.IndividualSessionFilesDetected);
        Assert.Equal(2, snapshot.EventCount);
        Assert.Equal(3, snapshot.NodeCount);
        Assert.NotNull(details);
        Assert.True(details.ContentTruncated);
        Assert.DoesNotContain("ghp_1234567890", details.Content, StringComparison.Ordinal);
        Assert.Equal("session_fs_nodes", details.StorageTable);
    }

    [Fact]
    public async Task TruncatedPreviewRedactsTokenFragmentsAtBoundary()
    {
        await new DatabaseInitializer(connectionFactory).InitializeAsync();
        string sessionId = $"diagnostics-{Guid.NewGuid():N}";
        ISessionFsHandler handler = new SqliteSessionFsProvider(connectionFactory, sessionId);
        Assert.Null(await handler.WriteFileAsync(new SessionFsWriteFileRequest
        {
            Path = "/session-state/token.txt",
            Content = "1234567890ghp_secret-value-continues",
        }, default));
        var reader = new SqliteSessionFsDiagnosticsReader(
            connectionFactory,
            Options.Create(new DiagnosticsOptions { MaximumPreviewCharacters = 18 }));

        SessionFsEntryDetails? details =
            await reader.GetEntryAsync(sessionId, "/session-state/token.txt");

        Assert.NotNull(details);
        Assert.True(details.ContentTruncated);
        Assert.Equal("1234567890ghp_[REDACTED]", details.Content);
    }

    public void Dispose() => connectionFactory.Dispose();
}
