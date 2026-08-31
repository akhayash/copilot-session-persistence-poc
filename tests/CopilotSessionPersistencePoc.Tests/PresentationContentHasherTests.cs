using System.IO.Compression;
using System.Text;
using CopilotSessionPersistencePoc.Execution;

namespace CopilotSessionPersistencePoc.Tests;

public sealed class PresentationContentHasherTests
{
    [Fact]
    public void HashIgnoresZipMetadata()
    {
        BinaryData first = CreateArchive(
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "<slide>same</slide>");
        BinaryData second = CreateArchive(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "<slide>same</slide>");

        Assert.NotEqual(
            Convert.ToHexString(first.ToArray()),
            Convert.ToHexString(second.ToArray()));
        Assert.Equal(
            PresentationContentHasher.Compute(first),
            PresentationContentHasher.Compute(second));
    }

    [Fact]
    public void HashChangesWithMemberContent()
    {
        BinaryData first = CreateArchive(DateTimeOffset.UtcNow, "<slide>first</slide>");
        BinaryData second = CreateArchive(DateTimeOffset.UtcNow, "<slide>second</slide>");

        Assert.NotEqual(
            PresentationContentHasher.Compute(first),
            PresentationContentHasher.Compute(second));
    }

    private static BinaryData CreateArchive(
        DateTimeOffset timestamp,
        string slideContent)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry slide = archive.CreateEntry(
                "ppt/slides/slide1.xml",
                CompressionLevel.Optimal);
            slide.LastWriteTime = timestamp;
            using var writer = new StreamWriter(
                slide.Open(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(slideContent);
        }

        return BinaryData.FromBytes(stream.ToArray());
    }
}
