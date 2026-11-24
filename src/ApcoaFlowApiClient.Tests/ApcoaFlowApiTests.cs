namespace ApcoaFlowApiClient.Tests;

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
