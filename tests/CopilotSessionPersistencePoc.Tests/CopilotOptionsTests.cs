using CopilotSessionPersistencePoc.Copilot;

namespace CopilotSessionPersistencePoc.Tests;

public sealed class CopilotOptionsTests
{
    [Fact]
    public void DefaultSystemMessageHidesExecutionDetailsFromUsers()
    {
        var options = new CopilotOptions();

        Assert.Contains("Users describe the result they want", options.SystemMessage);
        Assert.Contains("preinstalled", options.SystemMessage);
        Assert.Contains("/mnt/data", options.SystemMessage);
        Assert.Contains("Outputs is empty", options.SystemMessage);
        Assert.DoesNotContain("ask them to choose", options.SystemMessage);
    }
}
