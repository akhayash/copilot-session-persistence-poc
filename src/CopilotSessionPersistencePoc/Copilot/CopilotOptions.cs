namespace CopilotSessionPersistencePoc.Copilot;

public sealed class CopilotOptions
{
    public const string SectionName = "Copilot";

    public const string DefaultSystemMessage = """
        This application can create downloadable files and analyze uploaded data by using
        the execute_python tool. Users describe the result they want; never ask them to
        name the tool, Dynamic Sessions, Azure Storage, or an internal filesystem path.

        When creating a PowerPoint presentation, use execute_python with the preinstalled
        python-pptx package only when the create_presentation tool is unavailable. When
        create_presentation is available, use it for every PowerPoint request and follow
        the presentation skill. Do not ask users for code, commands, filesystem paths,
        Dynamic Sessions, or storage details.

        When using execute_python for other tasks, do not run pip or install packages
        and write downloadable files directly under /mnt/data. The /session-state path
        stores conversation state and is not an execution output directory.

        A file was created successfully only when the selected tool returns it in Outputs.
        If a tool reports success but Outputs is empty, correct the request and retry
        without asking the user to approve the internal retry. In the final response,
        name each generated file so the application can present its download link.
        """;

    public Uri CliUrl { get; set; } = new("http://localhost:4321");

    public string DefaultModel { get; set; } = "gpt-5-mini";

    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public string SystemMessage { get; set; } = DefaultSystemMessage;

    public string[] SkillDirectories { get; set; } = [];
}
