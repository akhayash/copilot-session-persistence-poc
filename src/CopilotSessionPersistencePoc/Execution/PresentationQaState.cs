namespace CopilotSessionPersistencePoc.Execution;

public sealed class PresentationQaState
{
    public string? PreviewPath { get; set; }

    public int PreviewCount { get; set; }

    public string? FirstPreviewSha256 { get; set; }

    public string? LastPreviewSha256 { get; set; }

    public DateTimeOffset? LastPreviewAt { get; set; }

    public string? PublishedPath { get; set; }

    public string? PublishedSha256 { get; set; }

    public string? PublishedArtifactId { get; set; }

    public string? PublishedFileName { get; set; }

    public void RecordPreview(string path, string sha256, DateTimeOffset previewedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (!string.Equals(PreviewPath, path, StringComparison.Ordinal)
            || (PublishedSha256 is not null
                && !string.Equals(PublishedSha256, sha256, StringComparison.OrdinalIgnoreCase)))
        {
            PreviewPath = path;
            PreviewCount = 0;
            FirstPreviewSha256 = null;
            LastPreviewSha256 = null;
            LastPreviewAt = null;
            PublishedPath = null;
            PublishedSha256 = null;
            PublishedArtifactId = null;
            PublishedFileName = null;
        }

        FirstPreviewSha256 ??= sha256;
        LastPreviewSha256 = sha256;
        LastPreviewAt = previewedAt;
        PreviewCount++;
    }

    public bool IsPublished(string path, string sha256) =>
        string.Equals(PublishedPath, path, StringComparison.Ordinal)
        && string.Equals(PublishedSha256, sha256, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(PublishedArtifactId)
        && !string.IsNullOrWhiteSpace(PublishedFileName);

    public void MarkPublished(
        string path,
        string sha256,
        string artifactId,
        string fileName)
    {
        PublishedPath = path;
        PublishedSha256 = sha256;
        PublishedArtifactId = artifactId;
        PublishedFileName = fileName;
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
