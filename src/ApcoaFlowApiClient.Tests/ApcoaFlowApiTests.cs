namespace ApcoaFlowApiClient.Tests;

/// <summary>
/// Live-API smoke test for authentication. Public-endpoint access and the session lifecycle
/// are covered by <see cref="LiveApiPublicEndpointTests"/> and <see cref="LiveApiSessionTests"/>.
/// </summary>
[Category("LiveApi")]
[NotInParallel(LiveApi.Serial)]
public class ApcoaFlowApiTests
{
    [Before(HookType.Class)]
    public static Task SweepBefore() => LiveApi.SweepOnceAtStartAsync();

    [After(HookType.Class)]
    public static Task SweepAfter() => LiveApi.EndActiveSessionsAsync("after-class");

    private readonly IApcoaFlowApi _api = ApiClientFactory.Api;

    [Test]
    public async Task Login_And_GetConsumer_Succeeds()
    {
        // Exercises the login flow (the auth handler logs in and attaches the token) via an
        // authenticated call, and confirms the consumer profile comes back for the test account.
        var consumer = await _api.GetConsumerAsync();

        await Assert.That(consumer).IsNotNull().Because("an authenticated consumer profile should be returned");
        await Assert.That(consumer.Id).IsGreaterThan(0).Because("the consumer must have a valid id");
        await Assert.That(consumer.EmailAddress).IsNotNull().Because("the profile should include the account email");
    }
}
