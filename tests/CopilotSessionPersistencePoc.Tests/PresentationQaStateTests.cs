using CopilotSessionPersistencePoc.Execution;

namespace CopilotSessionPersistencePoc.Tests;

public sealed class PresentationQaStateTests
{
    private static readonly DateTimeOffset PreviewedAt =
        new(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PublishRequiresTwoPreviews()
    {
        var state = new PresentationQaState();
        state.RecordPreview("deck.pptx", "aaa", PreviewedAt);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => state.EnsureCanPublish("deck.pptx", "aaa"));

        Assert.Contains("at least two successful previews", exception.Message);
        Assert.Contains("pptx_preview again", exception.Message);
    }

    [Fact]
    public void PublishRequiresAChangeBetweenPreviews()
    {
        var state = new PresentationQaState();
        state.RecordPreview("deck.pptx", "aaa", PreviewedAt);
        state.RecordPreview("deck.pptx", "AAA", PreviewedAt.AddMinutes(1));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => state.EnsureCanPublish("deck.pptx", "aaa"));

        Assert.Contains("not changed between previews", exception.Message);
        Assert.Contains("concrete correction", exception.Message);
    }

    [Fact]
    public void PublishRequiresPreviewOfCurrentContent()
    {
        var state = new PresentationQaState();
        state.RecordPreview("deck.pptx", "aaa", PreviewedAt);
        state.RecordPreview("deck.pptx", "bbb", PreviewedAt.AddMinutes(1));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => state.EnsureCanPublish("deck.pptx", "ccc"));

        Assert.Contains("changed after its latest preview", exception.Message);
        Assert.Contains("pptx_preview", exception.Message);
    }

    [Fact]
    public void PublishAllowsCorrectedAndRepreviewedContent()
    {
        var state = new PresentationQaState();
        state.RecordPreview("deck.pptx", "aaa", PreviewedAt);
        state.RecordPreview("deck.pptx", "bbb", PreviewedAt.AddMinutes(1));

        state.EnsureCanPublish("deck.pptx", "BBB");

        Assert.Equal(2, state.PreviewCount);
        Assert.Equal("deck.pptx", state.PreviewPath);
        Assert.Equal("aaa", state.FirstPreviewSha256);
        Assert.Equal("bbb", state.LastPreviewSha256);
        Assert.Equal(PreviewedAt.AddMinutes(1), state.LastPreviewAt);
    }

    [Fact]
    public void PreviewingAnotherFileStartsANewQaCycle()
    {
        var state = new PresentationQaState();
        state.RecordPreview("draft.pptx", "aaa", PreviewedAt);
        state.RecordPreview("final.pptx", "bbb", PreviewedAt.AddMinutes(1));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => state.EnsureCanPublish("final.pptx", "bbb"));

        Assert.Equal(1, state.PreviewCount);
        Assert.Equal("final.pptx", state.PreviewPath);
        Assert.Equal("bbb", state.FirstPreviewSha256);
        Assert.Contains("at least two successful previews", exception.Message);
    }

    [Fact]
    public void PublishedContentIsIdempotentlyRecognized()
    {
        var state = new PresentationQaState();
        state.RecordPreview("deck.pptx", "aaa", PreviewedAt);
        state.RecordPreview("deck.pptx", "bbb", PreviewedAt.AddMinutes(1));
        state.MarkPublished("deck.pptx", "bbb", "pptx-deck-bbb", "deck.pptx");

        Assert.True(state.IsPublished("deck.pptx", "BBB"));

        state.RecordPreview("deck.pptx", "ccc", PreviewedAt.AddMinutes(2));

        Assert.False(state.IsPublished("deck.pptx", "ccc"));
        Assert.Equal(1, state.PreviewCount);
        Assert.Equal("ccc", state.FirstPreviewSha256);
    }
}
