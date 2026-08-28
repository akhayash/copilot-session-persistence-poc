using CopilotSessionPersistencePoc.AppState;
using CopilotSessionPersistencePoc.SessionFs;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Copilot;

public sealed class CopilotSessionService(
    IAppSessionRepository sessions,
    ISessionFsProviderFactory sessionFsProviders,
    ICopilotClientFactory clientFactory,
    ISessionLockProvider lockProvider,
    IEnumerable<ICopilotToolProvider> toolProviders,
    IOptions<CopilotOptions> options,
    ILogger<CopilotSessionService> logger)
{
    private const string DefaultSessionTitle = "New session";
    private const int GeneratedTitleMaximumLength = 80;

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
        await using ISessionLockHandle sessionLock =
            await lockProvider.TryAcquireAsync(sessionId, cancellationToken);
        return await ExecuteWithLockAsync(
            sessionId,
            sessionLock,
            async operationToken =>
            {
                appSession = await GetRequiredSessionUnderLockAsync(
                    sessionId,
                    sessionLock,
                    operationToken);
                CopilotClient client = await clientFactory.GetClientAsync(operationToken);
                bool shouldResume = appSession.IsInitialized
                    || await sessionFsProviders.HasSessionStateAsync(
                        sessionId,
                        operationToken);

                await using CopilotSession copilotSession = shouldResume
                    ? await client.ResumeSessionAsync(
                        appSession.Id,
                        CreateResumeConfig(appSession),
                        operationToken)
                    : await client.CreateSessionAsync(
                        CreateSessionConfig(appSession),
                        operationToken);
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
                        operationToken);
                }
                catch (Exception exception)
                    when (exception is TimeoutException or OperationCanceledException)
                {
                    await copilotSession.AbortAsync(CancellationToken.None);
                    throw;
                }

                if (response?.Data?.Content is not { Length: > 0 } content)
                {
                    throw new InvalidOperationException(
                        "Copilot completed without returning an assistant message.");
                }

                await sessions.MarkInitializedAsync(
                    appSession.Id,
                    appSession.Version,
                    CreateGeneratedTitle(appSession, prompt),
                    operationToken);

                return content;
            },
            cancellationToken);
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

        await using ISessionLockHandle sessionLock =
            await lockProvider.TryAcquireAsync(sessionId, cancellationToken);
        return await ExecuteWithLockAsync(
            sessionId,
            sessionLock,
            async operationToken =>
            {
                appSession = await GetRequiredSessionUnderLockAsync(
                    sessionId,
                    sessionLock,
                    operationToken);
                CopilotClient client = await clientFactory.GetClientAsync(operationToken);
                await using CopilotSession copilotSession =
                    await client.ResumeSessionAsync(
                        appSession.Id,
                        CreateResumeConfig(appSession),
                        operationToken);
                IReadOnlyList<SessionEvent> events =
                    await copilotSession.GetEventsAsync(operationToken);
                CopilotMessage[] messages = events
                    .Select(ToMessage)
                    .Where(static message => message is not null)
                    .Cast<CopilotMessage>()
                    .ToArray();
                string? generatedTitle = appSession.Title.Equals(
                    DefaultSessionTitle,
                    StringComparison.Ordinal)
                        ? messages
                            .FirstOrDefault(static message => message.Role == "user")
                            is { Content: { Length: > 0 } firstPrompt }
                                ? CreateGeneratedTitle(appSession, firstPrompt)
                                : null
                        : null;

                if (!appSession.IsInitialized || generatedTitle is not null)
                {
                    await sessions.MarkInitializedAsync(
                        appSession.Id,
                        appSession.Version,
                        generatedTitle,
                        cancellationToken: operationToken);
                }

                return messages;
            },
            cancellationToken);
    }

    private static async Task<T> ExecuteWithLockAsync<T>(
        string sessionId,
        ISessionLockHandle sessionLock,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken callerToken)
    {
        using var operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                callerToken,
                sessionLock.LockLost);
        try
        {
            return await operation(operationCancellation.Token);
        }
        catch (OperationCanceledException)
            when (sessionLock.LockLost.IsCancellationRequested
                && !callerToken.IsCancellationRequested)
        {
            throw new IOException(
                $"Distributed lock for session '{sessionId}' was lost.");
        }
    }

    private SessionConfig CreateSessionConfig(AppSession appSession)
    {
        AIFunction[] tools = CreateTools(appSession.Id);
        return new SessionConfig
        {
            SessionId = appSession.Id,
            Model = appSession.Model,
            Tools = tools,
            AvailableTools = tools
                .Select(static tool => $"custom:{tool.Name}")
                .ToArray(),
            EnableSessionStore = false,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            CreateSessionFsProvider = context =>
                sessionFsProviders.Create(context.SessionId),
        };
    }

    private ResumeSessionConfig CreateResumeConfig(AppSession appSession)
    {
        AIFunction[] tools = CreateTools(appSession.Id);
        return new ResumeSessionConfig
        {
            Model = appSession.Model,
            Tools = tools,
            AvailableTools = tools
                .Select(static tool => $"custom:{tool.Name}")
                .ToArray(),
            EnableSessionStore = false,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            CreateSessionFsProvider = context =>
                sessionFsProviders.Create(context.SessionId),
        };
    }

    private AIFunction[] CreateTools(string sessionId) =>
        toolProviders
            .SelectMany(provider => provider.CreateTools(sessionId))
            .ToArray();

    private async Task<AppSession> GetRequiredSessionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        return await sessions.GetAsync(sessionId, cancellationToken)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
    }

    private async Task<AppSession> GetRequiredSessionUnderLockAsync(
        string sessionId,
        ISessionLockHandle sessionLock,
        CancellationToken cancellationToken)
    {
        AppSession? session = await sessions.GetAsync(sessionId, cancellationToken);
        if (session is not null)
        {
            return session;
        }

        sessionLock.DeleteOnRelease();
        throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
    }

    private static string? CreateGeneratedTitle(AppSession session, string prompt)
    {
        if (!session.Title.Equals(DefaultSessionTitle, StringComparison.Ordinal))
        {
            return null;
        }

        string compactTitle = string.Join(
            ' ',
            prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compactTitle.Length <= GeneratedTitleMaximumLength
            ? compactTitle
            : $"{compactTitle[..(GeneratedTitleMaximumLength - 3)]}...";
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
