using System.ComponentModel.DataAnnotations;

namespace CopilotSessionPersistencePoc.Execution;

public sealed class PresentationSessionsOptions
{
    public const string SectionName = "PresentationSessions";

    public bool Enabled { get; init; }

    public Uri? PoolManagementEndpoint { get; init; }

    [Required]
    public string ApiVersion { get; init; } = "2025-02-02-preview";

    [Range(1, 300)]
    public int RequestTimeoutSeconds { get; init; } = 180;

    [Range(1, 16)]
    public int MaximumFiles { get; init; } = 12;

    [Range(1, 134_217_728)]
    public int MaximumOutputBytes { get; init; } = 30 * 1024 * 1024;
}
