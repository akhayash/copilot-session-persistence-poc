using System.ComponentModel.DataAnnotations;

namespace CopilotSessionPersistencePoc.Persistence;

public sealed class AzureStorageOptions
{
    public const string SectionName = "AzureStorage";

    public string? ConnectionString { get; init; }

    public Uri? BlobServiceUri { get; init; }

    public Uri? TableServiceUri { get; init; }

    [RegularExpression("^[a-z0-9-]{3,63}$")]
    public string SessionFsContainer { get; init; } = "sessionfs";

    [RegularExpression("^[a-z0-9-]{3,63}$")]
    public string SessionLocksContainer { get; init; } = "session-locks";

    [RegularExpression("^[a-z0-9-]{3,63}$")]
    public string ArtifactsContainer { get; init; } = "artifacts";

    [RegularExpression("^[A-Za-z][A-Za-z0-9]{2,62}$")]
    public string AppSessionsTable { get; init; } = "appsessions";

    [RegularExpression("^[A-Za-z][A-Za-z0-9]{2,62}$")]
    public string ExecutionJobsTable { get; init; } = "executionjobs";

    [Range(1, 20)]
    public int MaximumWriteAttempts { get; init; } = 8;
}
