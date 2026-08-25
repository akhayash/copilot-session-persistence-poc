using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.Sqlite;

namespace CopilotSessionPersistencePoc.Api;

public sealed class SqliteBusyExceptionHandler : IExceptionHandler
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not SqliteException
            {
                SqliteErrorCode: SqliteBusy or SqliteLocked,
            })
        {
            return false;
        }

        await Results.Problem(
                title: "Persistence is temporarily busy",
                detail: "The SQLite busy timeout was exhausted. Retry the request.",
                statusCode: StatusCodes.Status503ServiceUnavailable)
            .ExecuteAsync(httpContext);
        return true;
    }
}
