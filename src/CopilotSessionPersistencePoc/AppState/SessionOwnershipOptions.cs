using System.ComponentModel.DataAnnotations;

namespace CopilotSessionPersistencePoc.AppState;

public sealed class SessionOwnershipOptions
{
    public const string SectionName = "SessionOwnership";

    public bool RequireAuthenticatedPrincipal { get; init; }

    [Required]
    public string LocalOwnerId { get; init; } = "local-user";
}
