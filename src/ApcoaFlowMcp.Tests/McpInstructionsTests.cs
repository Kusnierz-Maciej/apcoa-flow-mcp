namespace ApcoaFlowMcp.Tests;

/// <summary>The server instructions name the flow, the confirmation rules and what isn't supported (framework §4.1).</summary>
public class McpInstructionsTests
{
    [Test]
    public async Task Instructions_SayWhatIsNotSupported()
    {
        await Assert.That(McpInstructions.Text).Contains("Not supported:");
        await Assert.That(McpInstructions.Text).Contains("extending");
        await Assert.That(McpInstructions.Text).Contains("Say so instead of searching for");
    }

    [Test]
    public async Task Instructions_AskForConfirmation_AndTheReadableParkingName()
    {
        await Assert.That(McpInstructions.Text).Contains("confirm parking and license plate");
        await Assert.That(McpInstructions.Text).Contains("parkingName");
    }
}
