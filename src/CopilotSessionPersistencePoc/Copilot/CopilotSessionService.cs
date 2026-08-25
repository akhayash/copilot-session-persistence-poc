using CopilotSessionPersistencePoc.AppState;
using CopilotSessionPersistencePoc.SessionFs;
using GitHub.Copilot;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Copilot;

public sealed class CopilotSessionService(
    IAppSessionRepository sessions,
    ISessionFsProviderFactory sessionFsProviders,
    ICopilotClientFactory clientFactory,
    SessionLockProvider lockProvider,
    IOptions<CopilotOptions> options,
    ILogger<CopilotSessionService> logger)
{
    private static readonly Action<ILogger, string, string?, string?, Exception?> LogCopilotError =
        LoggerMessage.Define<string, string?, string?>(
            LogLevel.Error,
            new EventId(1, nameof(LogCopilotError)),
            "Copilot session {SessionId} reported {ErrorType}: {ErrorMessage}");

    private static readonly Action<ILogger, string, string, Exception?> LogCopilotEvent =
        LoggerMessage.Define<string, string>(
            LogLevel.Debug,
            new EventId(2, nameof(LogCopilotEvent)),
            "Copilot session {SessionId} event: {EventType}");

    public async Task<string> SendMessageAsync(
        string sessionId,
        string prompt,
        CancellationToken cancellationToken)
    {
        AppSession appSession = await GetRequiredSessionAsync(sessionId, cancellationToken);
        using IDisposable sessionLock = await lockProvider.TryAcquireAsync(sessionId, cancellationToken);
        CopilotClient client = await clientFactory.GetClientAsync(cancellationToken);
        bool shouldResume = appSession.IsInitialized
            || await sessionFsProviders.HasSessionStateAsync(sessionId, cancellationToken);

        await using CopilotSession copilotSession = shouldResume
            ? await client.ResumeSessionAsync(appSession.Id, CreateResumeConfig(appSession), cancellationToken)
            : await client.CreateSessionAsync(CreateSessionConfig(appSession), cancellationToken);
        using IDisposable subscription = copilotSession.On<SessionEvent>(sessionEvent =>
        {
            if (sessionEvent is SessionErrorEvent error)
            {
                LogCopilotError(
                    logger,
                    appSession.Id,
                    error.Data.ErrorType,
                    error.Data.Message,
                    null);
            }
            else if (logger.IsEnabled(LogLevel.Debug))
            {
                LogCopilotEvent(
                    logger,
                    appSession.Id,
                    sessionEvent.Type.ToString(),
                    null);
            }
        });

        AssistantMessageEvent? response;
        try
        {
            response = await copilotSession.SendAndWaitAsync(
                prompt,
                options.Value.ResponseTimeout,
                cancellationToken);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            await copilotSession.AbortAsync(CancellationToken.None);
            throw;
        }

        if (response?.Data?.Content is not { Length: > 0 } content)
        {
            throw new InvalidOperationException("Copilot completed without returning an assistant message.");
        }

        await sessions.MarkInitializedAsync(
            appSession.Id,
            appSession.Version,
            CancellationToken.None);

        return content;
    }

    public async Task<IReadOnlyList<CopilotMessage>> GetHistoryAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        AppSession appSession = await GetRequiredSessionAsync(sessionId, cancellationToken);
        bool hasPersistedState = appSession.IsInitialized
            || await sessionFsProviders.HasSessionStateAsync(sessionId, cancellationToken);
        if (!hasPersistedState)
        {
            return [];
        }

        using IDisposable sessionLock = await lockProvider.TryAcquireAsync(sessionId, cancellationToken);
        CopilotClient client = await clientFactory.GetClientAsync(cancellationToken);
        await using CopilotSession copilotSession =
            await client.ResumeSessionAsync(appSession.Id, CreateResumeConfig(appSession), cancellationToken);
        IReadOnlyList<SessionEvent> events = await copilotSession.GetEventsAsync(cancellationToken);
        if (!appSession.IsInitialized)
        {
            await sessions.MarkInitializedAsync(
                appSession.Id,
                appSession.Version,
                CancellationToken.None);
        }

        return events
            .Select(ToMessage)
            .Where(static message => message is not null)
            .Cast<CopilotMessage>()
            .ToArray();
    }

    private SessionConfig CreateSessionConfig(AppSession appSession) => new()
    {
        SessionId = appSession.Id,
        Model = appSession.Model,
        AvailableTools = [],
        EnableSessionStore = false,
        OnPermissionRequest = PermissionHandler.ApproveAll,
        CreateSessionFsProvider = context => sessionFsProviders.Create(context.SessionId),
    };

    private ResumeSessionConfig CreateResumeConfig(AppSession appSession) => new()
    {
        Model = appSession.Model,
        AvailableTools = [],
        EnableSessionStore = false,
        OnPermissionRequest = PermissionHandler.ApproveAll,
        CreateSessionFsProvider = context => sessionFsProviders.Create(context.SessionId),
    };

    private async Task<AppSession> GetRequiredSessionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        return await sessions.GetAsync(sessionId, cancellationToken)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
    }

    private static CopilotMessage? ToMessage(SessionEvent sessionEvent)
    {
        return sessionEvent switch
        {
            UserMessageEvent { Data.Content.Length: > 0 } user =>
                new CopilotMessage("user", user.Data.Content),
            AssistantMessageEvent { Data.Content.Length: > 0 } assistant =>
                new CopilotMessage("assistant", assistant.Data.Content),
            _ => null,
        };
    }
}
