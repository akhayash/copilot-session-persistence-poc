using CopilotSessionPersistencePoc.AppState;
using Microsoft.AspNetCore.Diagnostics;

namespace CopilotSessionPersistencePoc.Api;

public sealed class SessionOwnerExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not SessionOwnerUnavailableException)
        {
            return false;
        }

        await Results.Problem(
                title: "Authenticated user identity is unavailable",
                detail: "Sign in again before accessing sessions.",
                statusCode: StatusCodes.Status401Unauthorized)
            .ExecuteAsync(httpContext);
        return true;
    }
}
