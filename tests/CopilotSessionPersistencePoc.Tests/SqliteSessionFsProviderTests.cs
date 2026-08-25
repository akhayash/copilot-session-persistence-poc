using CopilotSessionPersistencePoc.Persistence;
using CopilotSessionPersistencePoc.SessionFs;
using GitHub.Copilot.Rpc;

namespace CopilotSessionPersistencePoc.Tests;

public sealed class SqliteSessionFsProviderTests : IDisposable
{
    private readonly TestSqliteConnectionFactory connectionFactory = new();

    [Fact]
    public async Task ProviderImplementsFileAndDirectoryContract()
    {
        await new DatabaseInitializer(connectionFactory).InitializeAsync();
        ISessionFsHandler handler = CreateProvider("session-a");

        Assert.Null(await handler.MkdirAsync(new SessionFsMkdirRequest
        {
            Path = "/workspace/nested",
            Recursive = true,
        }, default));
        Assert.Null(await handler.WriteFileAsync(new SessionFsWriteFileRequest
        {
            Path = "/workspace/nested/file.txt",
            Content = "hello",
        }, default));
        Assert.Null(await handler.AppendFileAsync(new SessionFsAppendFileRequest
        {
            Path = "/workspace/nested/file.txt",
            Content = " world",
        }, default));

        var content = await handler.ReadFileAsync(new SessionFsReadFileRequest
        {
            Path = "/workspace/nested/file.txt",
        }, default);
        var stat = await handler.StatAsync(new SessionFsStatRequest
        {
            Path = "/workspace/nested/file.txt",
        }, default);
        var entries = await handler.ReaddirWithTypesAsync(
            new SessionFsReaddirWithTypesRequest { Path = "/workspace/nested" },
            default);

        Assert.Null(content.Error);
        Assert.Equal("hello world", content.Content);
        Assert.True(stat.IsFile);
        Assert.Equal(11, stat.Size);
        var entry = Assert.Single(entries.Entries);
        Assert.Equal("file.txt", entry.Name);
        Assert.Equal(SessionFsReaddirWithTypesEntryType.File, entry.Type);

        Assert.Null(await handler.RenameAsync(new SessionFsRenameRequest
        {
            Src = "/workspace/nested/file.txt",
            Dest = "/archive/file.txt",
        }, default));
        var renamed = await handler.ReadFileAsync(new SessionFsReadFileRequest
        {
            Path = "/archive/file.txt",
        }, default);
        Assert.Equal("hello world", renamed.Content);

        Assert.Null(await handler.RmAsync(new SessionFsRmRequest
        {
            Path = "/archive",
            Recursive = true,
        }, default));
        var removed = await handler.ExistsAsync(new SessionFsExistsRequest
        {
            Path = "/archive/file.txt",
        }, default);
        Assert.False(removed.Exists);
    }

    [Fact]
    public async Task ProviderPersistsAcrossInstancesAndIsolatesSessions()
    {
        await new DatabaseInitializer(connectionFactory).InitializeAsync();
        ISessionFsHandler first = CreateProvider("session-a");
        Assert.Null(await first.WriteFileAsync(new SessionFsWriteFileRequest
        {
            Path = "/session-state/events.jsonl",
            Content = "{\"message\":\"persisted\"}\n",
        }, default));

        ISessionFsHandler second = CreateProvider("session-a");
        var restored = await second.ReadFileAsync(new SessionFsReadFileRequest
        {
            Path = "/session-state/events.jsonl",
        }, default);
        ISessionFsHandler otherSession = CreateProvider("session-b");
        var isolated = await otherSession.ExistsAsync(new SessionFsExistsRequest
        {
            Path = "/session-state/events.jsonl",
        }, default);

        Assert.Equal("{\"message\":\"persisted\"}\n", restored.Content);
        Assert.False(isolated.Exists);
    }

    [Fact]
    public async Task MissingFileMapsToEnoent()
    {
        await new DatabaseInitializer(connectionFactory).InitializeAsync();
        ISessionFsHandler handler = CreateProvider("session-a");
        var result = await handler.ReadFileAsync(
            new SessionFsReadFileRequest { Path = "/missing.txt" },
            default);

        Assert.NotNull(result.Error);
        Assert.Equal(SessionFsErrorCode.ENOENT, result.Error.Code);
    }

    [Fact]
    public async Task RenameRejectsMovingDirectoryIntoItsOwnSubtree()
    {
        await new DatabaseInitializer(connectionFactory).InitializeAsync();
        ISessionFsHandler handler = CreateProvider("session-a");
        Assert.Null(await handler.WriteFileAsync(new SessionFsWriteFileRequest
        {
            Path = "/source/file.txt",
            Content = "preserved",
        }, default));

        SessionFsError? error = await handler.RenameAsync(new SessionFsRenameRequest
        {
            Src = "/source",
            Dest = "/source/nested",
        }, default);

        Assert.NotNull(error);
        var source = await handler.ReadFileAsync(new SessionFsReadFileRequest
        {
            Path = "/source/file.txt",
        }, default);
        Assert.Equal("preserved", source.Content);
    }

    private SqliteSessionFsProvider CreateProvider(string sessionId) =>
        new(connectionFactory, sessionId);

    public void Dispose() => connectionFactory.Dispose();
}
