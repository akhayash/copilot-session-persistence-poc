using CopilotSessionPersistencePoc.Copilot;

namespace CopilotSessionPersistencePoc.Tests;

public sealed class CopilotOptionsTests
{
    [Fact]
    public void DefaultSystemMessageHidesExecutionDetailsFromUsers()
    {
        var options = new CopilotOptions();

        Assert.Contains("Users describe the result they want", options.SystemMessage);
        Assert.Contains("/mnt/data", options.SystemMessage);
        Assert.Contains("Outputs is empty", options.SystemMessage);
        Assert.Contains("preview again", options.SystemMessage);
        Assert.Contains("Do not use execute_python for PowerPoint", options.SystemMessage);
        Assert.DoesNotContain("use it for every PowerPoint request", options.SystemMessage);
        Assert.DoesNotContain("ask them to choose", options.SystemMessage);
    }
}
