using CopilotSessionPersistencePoc.SessionFs;

namespace CopilotSessionPersistencePoc.Tests;

public sealed class SessionFsPathTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("workspace/file.txt", "/workspace/file.txt")]
    [InlineData("/workspace/file.txt/", "/workspace/file.txt")]
    public void ParseNormalizesValidPaths(string input, string expected)
    {
        Assert.Equal(expected, SessionFsPath.Parse(input).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/workspace//file.txt")]
    [InlineData("/workspace/../secret.txt")]
    [InlineData("/workspace/./file.txt")]
    [InlineData("C:\\secret.txt")]
    public void ParseRejectsUnsafePaths(string input)
    {
        Assert.ThrowsAny<ArgumentException>(() => SessionFsPath.Parse(input));
    }

    [Fact]
    public void AncestorsReturnsParentDirectories()
    {
        var ancestors = SessionFsPath.Parse("/a/b/file.txt").Ancestors();

        Assert.Equal(["/", "/a", "/a/b"], ancestors);
    }
}
