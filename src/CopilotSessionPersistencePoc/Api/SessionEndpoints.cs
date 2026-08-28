using CopilotSessionPersistencePoc.AppState;
using CopilotSessionPersistencePoc.ArtifactStorage;
using CopilotSessionPersistencePoc.Copilot;
using CopilotSessionPersistencePoc.Diagnostics;
using CopilotSessionPersistencePoc.Execution;
using CopilotSessionPersistencePoc.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using System.Net.Sockets;

namespace CopilotSessionPersistencePoc.Api;

public static class SessionEndpoints
{
    private const int MaxTitleLength = 120;
    private const int MaxPromptLength = 16_000;
    private const int MaxArtifactUploadBytes = 10 * 1024 * 1024;

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
        api.MapGet("/sessions/{id}/artifacts", ListArtifactsAsync);
        api.MapPost("/sessions/{id}/artifacts", UploadArtifactAsync);
        api.MapGet(
            "/sessions/{id}/artifacts/{artifactId}/{fileName}",
            DownloadArtifactAsync);
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

    private static async Task<IResult> ListArtifactsAsync(
        string id,
        IAppSessionRepository repository,
        IArtifactStore artifacts,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (await repository.GetAsync(id, cancellationToken) is null)
        {
            return Results.NotFound();
        }

        try
        {
            IReadOnlyList<ArtifactInfo> items =
                await artifacts.ListAsync(id, cancellationToken);
            IReadOnlyList<ArtifactInfo> published = await FilterPublishedArtifactsAsync(
                id,
                items,
                services.GetService<IExecutionJobRepository>(),
                cancellationToken);
            return Results.Ok(published.Select(ArtifactResponse.From));
        }
        catch (ArtifactStorageUnavailableException exception)
        {
            return ArtifactUnavailable(exception);
        }
    }

    private static async Task<IResult> UploadArtifactAsync(
        string id,
        string fileName,
        HttpRequest request,
        IAppSessionRepository repository,
        IArtifactStore artifacts,
        CancellationToken cancellationToken)
    {
        if (await repository.GetAsync(id, cancellationToken) is null)
        {
            return Results.NotFound();
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return ValidationProblem(nameof(fileName), "A file name is required.");
        }

        if (request.ContentLength is > MaxArtifactUploadBytes)
        {
            return Results.Problem(
                title: "Artifact is too large",
                detail: $"Artifact uploads must not exceed {MaxArtifactUploadBytes} bytes.",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        try
        {
            string contentType = request.ContentType ?? "application/octet-stream";
            if (!MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? parsedType))
            {
                return ValidationProblem(
                    nameof(request.ContentType),
                    "The artifact Content-Type is invalid.");
            }

            BinaryData content = await ReadBodyAsync(
                request.Body,
                MaxArtifactUploadBytes,
                cancellationToken);
            ArtifactInfo uploaded = await artifacts.PutAsync(
                id,
                $"upload-{Guid.NewGuid():N}",
                fileName,
                parsedType.MediaType.ToString(),
                content,
                cancellationToken);
            return Results.Created(
                $"/api/sessions/{Uri.EscapeDataString(id)}"
                + $"/artifacts/{Uri.EscapeDataString(uploaded.ArtifactId)}"
                + $"/{Uri.EscapeDataString(uploaded.FileName)}",
                ArtifactResponse.From(uploaded));
        }
        catch (ArtifactStorageUnavailableException exception)
        {
            return ArtifactUnavailable(exception);
        }
        catch (ArgumentException exception)
        {
            return ValidationProblem(nameof(fileName), exception.Message);
        }
        catch (ArtifactTooLargeException exception)
        {
            return Results.Problem(
                title: "Artifact is too large",
                detail: exception.Message,
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }
    }

    private static async Task<IResult> DownloadArtifactAsync(
        string id,
        string artifactId,
        string fileName,
        IAppSessionRepository repository,
        IArtifactStore artifacts,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (await repository.GetAsync(id, cancellationToken) is null)
        {
            return Results.NotFound();
        }

        try
        {
            if (IsGeneratedArtifact(artifactId)
                && !await IsSucceededAsync(
                    id,
                    artifactId,
                    services.GetService<IExecutionJobRepository>(),
                    cancellationToken))
            {
                return Results.NotFound();
            }

            ArtifactContent? artifact = await artifacts.GetAsync(
                id,
                artifactId,
                fileName,
                cancellationToken);
            return artifact is null
                ? Results.NotFound()
                : Results.File(
                    artifact.Content.ToArray(),
                    artifact.Info.ContentType,
                    artifact.Info.FileName);
        }
        catch (ArtifactStorageUnavailableException exception)
        {
            return ArtifactUnavailable(exception);
        }
        catch (ArgumentException exception)
        {
            return ValidationProblem(nameof(fileName), exception.Message);
        }
    }

    private static async Task<IResult> GetHealth(
        IOptions<CopilotOptions> options,
        IOptions<PersistenceOptions> persistence,
        IOptions<DynamicSessionsOptions> dynamicSessions,
        CancellationToken cancellationToken)
    {
        Uri cliUrl = options.Value.CliUrl;
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(cliUrl.Host, cliUrl.Port, cancellationToken);
            return Results.Ok(
                new HealthResponse(
                    "healthy",
                    persistence.Value.Backend,
                    "reachable",
                    dynamicSessions.Value.Enabled ? "DynamicSessions" : "disabled"));
        }
        catch (SocketException)
        {
            return Results.Json(
                new HealthResponse(
                    "degraded",
                    persistence.Value.Backend,
                    "unreachable",
                    dynamicSessions.Value.Enabled ? "DynamicSessions" : "disabled"),
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

    private static IResult ArtifactUnavailable(
        ArtifactStorageUnavailableException exception) =>
        Results.Problem(
            title: "Artifact storage is unavailable",
            detail: exception.Message,
            statusCode: StatusCodes.Status409Conflict);

    private static async Task<IReadOnlyList<ArtifactInfo>> FilterPublishedArtifactsAsync(
        string sessionId,
        IReadOnlyList<ArtifactInfo> artifacts,
        IExecutionJobRepository? jobs,
        CancellationToken cancellationToken)
    {
        var published = new List<ArtifactInfo>(artifacts.Count);
        foreach (IGrouping<string, ArtifactInfo> group in artifacts.GroupBy(
            static artifact => artifact.ArtifactId,
            StringComparer.Ordinal))
        {
            if (!IsGeneratedArtifact(group.Key)
                || await IsSucceededAsync(
                    sessionId,
                    group.Key,
                    jobs,
                    cancellationToken))
            {
                published.AddRange(group);
            }
        }

        return published;
    }

    private static async Task<bool> IsSucceededAsync(
        string sessionId,
        string artifactId,
        IExecutionJobRepository? jobs,
        CancellationToken cancellationToken) =>
        jobs is not null
        && (await jobs.GetByJobIdAsync(sessionId, artifactId, cancellationToken))?.Status
            == ExecutionJobStatus.Succeeded;

    private static bool IsGeneratedArtifact(string artifactId) =>
        artifactId.StartsWith("job-", StringComparison.Ordinal);

    private static async Task<BinaryData> ReadBodyAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var content = new MemoryStream();
        byte[] buffer = new byte[81_920];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return BinaryData.FromBytes(content.ToArray());
            }

            if (content.Length + read > maximumBytes)
            {
                throw new ArtifactTooLargeException(maximumBytes);
            }

            await content.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private sealed class ArtifactTooLargeException(int maximumBytes)
        : IOException($"Artifact uploads must not exceed {maximumBytes} bytes.");
}
