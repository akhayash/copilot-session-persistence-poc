namespace CopilotSessionPersistencePoc.Diagnostics;

public sealed class DiagnosticsOptions
{
    public const string SectionName = "Diagnostics";

    public bool Enabled { get; init; } = true;

    public int MaximumPreviewCharacters { get; init; } = 65_536;
}
