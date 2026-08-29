using System.ComponentModel;
using CopilotSessionPersistencePoc.Copilot;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace CopilotSessionPersistencePoc.Execution;

public sealed class PresentationToolProvider(
    PresentationExecutionCoordinator coordinator)
    : ICopilotToolProvider
{
    public IReadOnlyList<AIFunction> CreateTools(string sessionId) =>
    [
        CopilotTool.DefineTool(
            async (
                [Description("Output .pptx file name without a directory.")]
                string fileName,
                [Description("Presentation title.")]
                string title,
                [Description("Optional subtitle for the title slide.")]
                string? subtitle,
                [Description("Audience for tone and terminology.")]
                string audience,
                [Description(
                    "Titles for one to seven content slides. A title slide is added automatically.")]
                string[] slideTitles,
                [Description(
                    "Body text corresponding one-to-one with slideTitles. "
                    + "Use concise newline-separated points.")]
                string[] slideBodies,
                [Description(
                    "Optional short highlights corresponding one-to-one with slideTitles. "
                    + "Use an empty string when a slide has no highlight.")]
                string[]? slideHighlights,
                ToolInvocation invocation,
                CancellationToken cancellationToken) =>
            {
                EnsureSession(invocation, sessionId);
                if (slideTitles.Length != slideBodies.Length
                    || slideHighlights is not null
                        && slideHighlights.Length != slideTitles.Length)
                {
                    throw new ArgumentException(
                        "slideTitles, slideBodies, and slideHighlights must have equal lengths.");
                }

                PresentationSlide[] slides = slideTitles
                    .Select((slideTitle, index) => new PresentationSlide(
                        slideTitle,
                        slideBodies[index],
                        string.IsNullOrWhiteSpace(slideHighlights?[index])
                            ? null
                            : slideHighlights[index]))
                    .ToArray();
                return await coordinator.ExecuteAsync(
                    sessionId,
                    invocation.ToolCallId,
                    new PresentationWorkerRequest(
                        fileName,
                        title,
                        subtitle,
                        audience,
                        slides),
                    cancellationToken);
            },
            toolOptions: new CopilotToolOptions
            {
                Defer = CopilotToolDefer.Never,
                SkipPermission = true,
            },
            factoryOptions: new AIFunctionFactoryOptions
            {
                Name = "create_presentation",
                Description =
                    "Create and validate a designed PowerPoint presentation from structured "
                    + "content. The service generates PPTX and PDF files, renders one PNG "
                    + "preview per slide, and returns a validation manifest. Users do not "
                    + "need to provide code, commands, filesystem paths, or storage details.",
            }),
    ];

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
