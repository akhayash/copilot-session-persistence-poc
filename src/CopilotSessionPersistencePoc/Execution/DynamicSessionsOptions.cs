using System.ComponentModel.DataAnnotations;

namespace CopilotSessionPersistencePoc.Execution;

public sealed class DynamicSessionsOptions
{
    public const string SectionName = "DynamicSessions";

    public bool Enabled { get; init; }

    public Uri? PoolManagementEndpoint { get; init; }

    [Required]
    public string ApiVersion { get; init; } = "2025-10-02-preview";

    [Range(1, 220)]
    public int ExecutionTimeoutSeconds { get; init; } = 180;

    public TimeSpan StaleJobTimeout { get; init; } = TimeSpan.FromMinutes(5);

    [Range(1, 100_000)]
    public int MaximumCodeCharacters { get; init; } = 32_000;

    [Range(1, 100)]
    public int MaximumInputFiles { get; init; } = 10;

    [Range(1, 134_217_728)]
    public int MaximumInputBytes { get; init; } = 10 * 1024 * 1024;

    [Range(1, 100)]
    public int MaximumOutputFiles { get; init; } = 20;

    [Range(1, 134_217_728)]
    public int MaximumOutputBytes { get; init; } = 20 * 1024 * 1024;

    [Range(1, 65_536)]
    public int MaximumCapturedCharacters { get; init; } = 16_000;
}
