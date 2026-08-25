namespace CopilotSessionPersistencePoc.Copilot;

public sealed class CopilotOptions
{
    public const string SectionName = "Copilot";

    public Uri CliUrl { get; set; } = new("http://localhost:4321");

    public string DefaultModel { get; set; } = "gpt-5-mini";

    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
