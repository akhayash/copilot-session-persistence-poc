using CopilotSessionPersistencePoc.AppState;
using CopilotSessionPersistencePoc.Copilot;
using CopilotSessionPersistencePoc.Diagnostics;
using CopilotSessionPersistencePoc.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Net.Sockets;

namespace CopilotSessionPersistencePoc.Api;

public static class SessionEndpoints
{
    private const int MaxTitleLength = 120;
    private const int MaxPromptLength = 16_000;

    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder api = endpoints.MapGroup("/api");

        api.MapGet("/sessions", ListSessionsAsync);
        api.MapPost("/sessions", CreateSessionAsync);
        api.MapGet("/sessions/{id}", GetSessionAsync);
        api.MapPost("/sessions/{id}/messages", SendMessageAsync);
        api.MapDelete("/sessions/{id}", DeleteSessionAsync);
        api.MapGet("/sessions/{id}/diagnostics", GetDiagnosticsAsync);
        api.MapGet("/sessions/{id}/diagnostics/entry", GetDiagnosticEntryAsync);
        api.MapGet("/health", GetHealth);

        return endpoints;
    }

    private static async Task<IResult> ListSessionsAsync(
        IAppSessionRepository repository,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AppSession> sessions = await repository.ListAsync(cancellationToken);
        return Results.Ok(sessions.Select(SessionResponse.From));
    }

    private static async Task<IResult> CreateSessionAsync(
        CreateSessionRequest request,
        IAppSessionRepository repository,
        IOptions<CopilotOptions> options,
        CancellationToken cancellationToken)
    {
        string title = string.IsNullOrWhiteSpace(request.Title) ? "New session" : request.Title.Trim();
        if (title.Length > MaxTitleLength)
        {
            return ValidationProblem(nameof(request.Title), $"Title must not exceed {MaxTitleLength} characters.");
        }

        string model = string.IsNullOrWhiteSpace(request.Model)
            ? options.Value.DefaultModel
            : request.Model.Trim();
        AppSession created = await repository.CreateAsync(
            $"session-{Guid.NewGuid():N}",
            title,
            model,
            cancellationToken);

        return Results.Created($"/api/sessions/{created.Id}", SessionResponse.From(created));
    }

    private static async Task<IResult> GetSessionAsync(
        string id,
        IAppSessionRepository repository,
        CopilotSessionService copilot,
        CancellationToken cancellationToken)
    {
        AppSession? session = await repository.GetAsync(id, cancellationToken);
        if (session is null)
        {
            return Results.NotFound();
        }

        try
        {
            IReadOnlyList<CopilotMessage> messages = await copilot.GetHistoryAsync(id, cancellationToken);
            session = await repository.GetAsync(id, cancellationToken);
            if (session is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(new SessionDetailsResponse(SessionResponse.From(session), messages));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (SessionBusyException exception)
        {
            return Results.Conflict(new ProblemDetails
            {
                Title = "Session is busy",
                Detail = exception.Message,
                Status = StatusCodes.Status409Conflict,
            });
        }
        catch (SessionConcurrencyException exception)
        {
            return Results.Conflict(new ProblemDetails
            {
                Title = "Session was modified concurrently",
                Detail = exception.Message,
                Status = StatusCodes.Status409Conflict,
            });
        }
        catch (IOException exception)
        {
            return CopilotUnavailable(exception);
        }
    }

    private static async Task<IResult> SendMessageAsync(
        string id,
        SendMessageRequest request,
        CopilotSessionService copilot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > MaxPromptLength)
        {
            return ValidationProblem(
                nameof(request.Prompt),
                $"Prompt is required and must not exceed {MaxPromptLength} characters.");
        }

        try
        {
            string content = await copilot.SendMessageAsync(id, request.Prompt.Trim(), cancellationToken);
            return Results.Ok(new SendMessageResponse(new CopilotMessage("assistant", content)));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (SessionBusyException exception)
        {
            return Results.Conflict(new ProblemDetails
            {
                Title = "Session is busy",
                Detail = exception.Message,
                Status = StatusCodes.Status409Conflict,
            });
        }
        catch (SessionConcurrencyException exception)
        {
            return Results.Conflict(new ProblemDetails
            {
                Title = "Session was modified concurrently",
                Detail = exception.Message,
                Status = StatusCodes.Status409Conflict,
            });
        }
        catch (TimeoutException exception)
        {
            return Results.Problem(
                title: "Copilot response timed out",
                detail: exception.Message,
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (IOException exception)
        {
            return CopilotUnavailable(exception);
        }
    }

    private static async Task<IResult> DeleteSessionAsync(
        string id,
        IAppSessionRepository repository,
        ISessionLockProvider lockProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await repository.ExistsForDeletionAsync(id, cancellationToken))
            {
                return Results.NoContent();
            }

            await using ISessionLockHandle sessionLock =
                await lockProvider.TryAcquireAsync(id, cancellationToken);
            using var operationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    sessionLock.LockLost);
            await repository.DeleteAsync(id, operationCancellation.Token);
            sessionLock.DeleteOnRelease();
            return Results.NoContent();
        }
        catch (SessionBusyException exception)
        {
            return Results.Conflict(new ProblemDetails
            {
                Title = "Session is busy",
                Detail = exception.Message,
                Status = StatusCodes.Status409Conflict,
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem(
                title: "Session deletion did not complete",
                detail: "The distributed session lock was lost before deletion completed.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (IOException exception)
        {
            return Results.Problem(
                title: "Session deletion did not complete",
                detail: exception.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> GetDiagnosticsAsync(
        string id,
        IAppSessionRepository repository,
        ISessionFsDiagnosticsReader diagnostics,
        IOptions<DiagnosticsOptions> options,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return Results.NotFound();
        }

        if (await repository.GetAsync(id, cancellationToken) is null)
        {
            return Results.NotFound();
        }

        SessionFsDiagnosticsSnapshot snapshot =
            await diagnostics.GetSnapshotAsync(id, cancellationToken);
        return Results.Ok(snapshot);
    }

    private static async Task<IResult> GetDiagnosticEntryAsync(
        string id,
        string path,
        IAppSessionRepository repository,
        ISessionFsDiagnosticsReader diagnostics,
        IOptions<DiagnosticsOptions> options,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return Results.NotFound();
        }

        if (await repository.GetAsync(id, cancellationToken) is null)
        {
            return Results.NotFound();
        }

        try
        {
            SessionFsEntryDetails? entry =
                await diagnostics.GetEntryAsync(id, path, cancellationToken);
            return entry is null ? Results.NotFound() : Results.Ok(entry);
        }
        catch (ArgumentException exception)
        {
            return ValidationProblem(nameof(path), exception.Message);
        }
    }

    private static async Task<IResult> GetHealth(
        IOptions<CopilotOptions> options,
        IOptions<PersistenceOptions> persistence,
        CancellationToken cancellationToken)
    {
        Uri cliUrl = options.Value.CliUrl;
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(cliUrl.Host, cliUrl.Port, cancellationToken);
            return Results.Ok(
                new HealthResponse("healthy", persistence.Value.Backend, "reachable"));
        }
        catch (SocketException)
        {
            return Results.Json(
                new HealthResponse("degraded", persistence.Value.Backend, "unreachable"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static IResult ValidationProblem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [field] = [message],
        });

    private static IResult CopilotUnavailable(IOException exception) =>
        Results.Problem(
            title: "Copilot CLI is unavailable",
            detail: exception.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
