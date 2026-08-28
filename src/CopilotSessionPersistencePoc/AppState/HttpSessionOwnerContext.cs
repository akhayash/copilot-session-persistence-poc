using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.AppState;

public sealed class HttpSessionOwnerContext(
    IHttpContextAccessor httpContextAccessor,
    IOptions<SessionOwnershipOptions> options)
    : ISessionOwnerContext
{
    public const string PrincipalIdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    public string OwnerKey
    {
        get
        {
            string? principalId = httpContextAccessor.HttpContext?
                .Request.Headers[PrincipalIdHeader]
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(principalId))
            {
                if (options.Value.RequireAuthenticatedPrincipal)
                {
                    throw new SessionOwnerUnavailableException();
                }

                principalId = options.Value.LocalOwnerId;
            }

            return SessionOwnerKey.Create(principalId);
        }
    }
}

public static class SessionOwnerKey
{
    public static string Create(string principalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(principalId.Trim())));
    }
}

public sealed class SessionOwnerUnavailableException()
    : InvalidOperationException(
        "The authenticated user identity header is missing.");
