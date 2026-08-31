namespace CopilotSessionPersistencePoc.Execution;

public sealed class PresentationQaState
{
    public string? PreviewPath { get; set; }

    public int PreviewCount { get; set; }

    public string? FirstPreviewSha256 { get; set; }

    public string? LastPreviewSha256 { get; set; }

    public DateTimeOffset? LastPreviewAt { get; set; }

    public void RecordPreview(string path, string sha256, DateTimeOffset previewedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (!string.Equals(PreviewPath, path, StringComparison.Ordinal))
        {
            PreviewPath = path;
            PreviewCount = 0;
            FirstPreviewSha256 = null;
            LastPreviewSha256 = null;
            LastPreviewAt = null;
        }

        FirstPreviewSha256 ??= sha256;
        LastPreviewSha256 = sha256;
        LastPreviewAt = previewedAt;
        PreviewCount++;
    }

    public void EnsureCanPublish(string path, string currentSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSha256);
        if (!string.Equals(PreviewPath, path, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The selected presentation has not been previewed. "
                + "Call pptx_preview twice for this PPTX, making a concrete correction "
                + "between previews, before publishing.");
        }

        if (PreviewCount < 2)
        {
            throw new InvalidOperationException(
                "Publishing requires at least two successful previews. "
                + "Inspect the first preview, correct the presentation, then call "
                + "pptx_preview again before publishing.");
        }

        if (string.Equals(
            FirstPreviewSha256,
            LastPreviewSha256,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The presentation was not changed between previews. "
                + "Make a concrete correction based on the rendered slides, regenerate "
                + "the PPTX, and call pptx_preview again before publishing.");
        }

        if (!string.Equals(
            LastPreviewSha256,
            currentSha256,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The presentation changed after its latest preview. "
                + "Call pptx_preview for the current PPTX before publishing.");
        }
    }
}
