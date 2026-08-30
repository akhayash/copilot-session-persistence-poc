using System.ComponentModel;
using System.Text.Json;
using CopilotSessionPersistencePoc.Copilot;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace CopilotSessionPersistencePoc.Execution;

public sealed class PresentationWorkspaceToolProvider(
    PresentationWorkspaceCoordinator coordinator)
    : ICopilotToolProvider
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public IReadOnlyList<AIFunction> CreateTools(string sessionId) =>
    [
        CopilotTool.DefineTool(
            async (
                [Description("Stable deck workspace ID using letters, digits, '-' or '_'.")]
                string deckId,
                [Description("Shell command to execute from the deck workspace root.")]
                string command,
                ToolInvocation invocation,
                CancellationToken cancellationToken) =>
            {
                EnsureSession(invocation, sessionId);
                return await coordinator.ExecuteAsync(
                    sessionId,
                    deckId,
                    command,
                    cancellationToken);
            },
            ToolOptions,
            new AIFunctionFactoryOptions
            {
                Name = "pptx_run",
                Description =
                    "Run shell or Python commands inside a persistent PowerPoint workspace. "
                    + "The sandbox has no outbound network access. Use preinstalled packages only.",
            }),
        CopilotTool.DefineTool(
            async (
                [Description("Stable deck workspace ID.")]
                string deckId,
                [Description("Operation: list, read, or write.")]
                string operation,
                [Description("Relative workspace path for read/write.")]
                string? path,
                [Description("UTF-8 text content for write.")]
                string? content,
                ToolInvocation invocation,
                CancellationToken cancellationToken) =>
            {
                EnsureSession(invocation, sessionId);
                return operation.ToLowerInvariant() switch
                {
                    "list" => JsonSerializer.Serialize(
                        await coordinator.ListFilesAsync(
                            sessionId,
                            deckId,
                            cancellationToken),
                        JsonOptions),
                    "read" when path is not null => await coordinator.ReadTextAsync(
                        sessionId,
                        deckId,
                        path,
                        cancellationToken),
                    "write" when path is not null && content is not null =>
                        JsonSerializer.Serialize(
                            await coordinator.WriteTextAsync(
                                sessionId,
                                deckId,
                                path,
                                content,
                                cancellationToken),
                            JsonOptions),
                    _ => throw new ArgumentException(
                        "operation must be list, read, or write; path/content are required as appropriate."),
                };
            },
            ToolOptions,
            new AIFunctionFactoryOptions
            {
                Name = "pptx_files",
                Description =
                    "List, read, or write UTF-8 files in a persistent PowerPoint workspace. "
                    + "Use pptx_run for generated binary files.",
            }),
        CopilotTool.DefineTool(
            async (
                [Description("Stable deck workspace ID.")]
                string deckId,
                [Description("Relative path to the .pptx file to inspect.")]
                string path,
                ToolInvocation invocation,
                CancellationToken cancellationToken) =>
            {
                EnsureSession(invocation, sessionId);
                PresentationRenderResult result = await coordinator.RenderAsync(
                    sessionId,
                    deckId,
                    path,
                    cancellationToken);
                return new ToolResultAIContent(new ToolResultObject
                {
                    ResultType = "success",
                    TextResultForLlm =
                        $"Validated {result.SlideCount} slides. Inspect every returned image.",
                    BinaryResultsForLlm =
                    [
                        .. result.Images.Select(static image => new ToolBinaryResult
                        {
                            Type = ToolBinaryResultType.Image,
                            MimeType = image.MimeType,
                            Data = Convert.ToBase64String(image.Content.ToArray()),
                            Description = $"Rendered slide {image.SlideNumber}",
                        }),
                    ],
                });
            },
            ToolOptions,
            new AIFunctionFactoryOptions
            {
                Name = "pptx_preview",
                Description =
                    "Validate and render a workspace .pptx, returning every slide as an image "
                    + "for visual inspection. Always use this before publishing.",
            }),
        CopilotTool.DefineTool(
            async (
                [Description("Stable deck workspace ID.")]
                string deckId,
                [Description("Relative path to the final .pptx file.")]
                string path,
                ToolInvocation invocation,
                CancellationToken cancellationToken) =>
            {
                EnsureSession(invocation, sessionId);
                return await coordinator.PublishAsync(
                    sessionId,
                    deckId,
                    invocation.ToolCallId,
                    path,
                    cancellationToken);
            },
            ToolOptions,
            new AIFunctionFactoryOptions
            {
                Name = "pptx_publish",
                Description =
                    "Validate and publish the final .pptx as a downloadable session artifact. "
                    + "Call only after pptx_preview and any required corrections.",
            }),
    ];

    private static CopilotToolOptions ToolOptions => new()
    {
        Defer = CopilotToolDefer.Never,
        SkipPermission = true,
    };

    private static void EnsureSession(ToolInvocation invocation, string expectedSessionId)
    {
        if (!string.Equals(
            invocation.SessionId,
            expectedSessionId,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The tool invocation does not belong to the active session.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(invocation.ToolCallId);
    }
}
