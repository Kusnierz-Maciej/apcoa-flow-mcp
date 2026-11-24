namespace ApcoaFlowApiClient.Tests;

// Placeholder live-API tests (still stubbed). The constant-assertion analyzer warning is
// expected until these are implemented; suppress it narrowly for this file.
#pragma warning disable TUnitAssertions0005
[Category("LiveApi")]
public class ApcoaFlowApiTests
{
    private readonly IApcoaFlowApi _api = ApiClientFactory.Api;

    [Test]
    public async Task Login_WithValidCredentials_Succeeds()
    {
        // TODO: implement once API is known
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task GetParkingZones_ReturnsSuccessfully()
    {
        // TODO: implement once API is known
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task StartSession_AndEndSession_Succeeds()
    {
        // TODO: implement once API is known
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task GetActiveSession_WhenNoSession_ReturnsNull()
    {
        // TODO: implement once API is known
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task GetSessionHistory_ReturnsSuccessfully()
    {
        // TODO: implement once API is known
        await Assert.That(true).IsTrue();
    }
}
#pragma warning restore TUnitAssertions0005
