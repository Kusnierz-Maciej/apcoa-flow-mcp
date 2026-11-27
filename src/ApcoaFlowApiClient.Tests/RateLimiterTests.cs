using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace ApcoaFlowApiClient.Tests;

public class RateLimiterTests
{
    [Test]
    public async Task Burst_UpToTokenLimit_IsImmediate_ThenQueues()
    {
        using var limiter = new ApcoaFlowRateLimiter();

        for (var i = 0; i < ApcoaFlowRateLimiter.TokenLimit; i++)
        {
            using var lease = limiter.Limiter.AttemptAcquire();
            await Assert.That(lease.IsAcquired).IsTrue();
        }

        // Bucket empty: an immediate attempt fails, but an awaited acquire queues until a token refills.
        using (var rejected = limiter.Limiter.AttemptAcquire())
            await Assert.That(rejected.IsAcquired).IsFalse();

        var sw = Stopwatch.StartNew();
        using var queued = await limiter.Limiter.AcquireAsync();
        sw.Stop();

        await Assert.That(queued.IsAcquired).IsTrue();
        await Assert.That(sw.Elapsed).IsLessThan(ApcoaFlowRateLimiter.ReplenishmentPeriod * 4);
    }

    [Test]
    public async Task LoginClient_HasBaseAddressAndAppHeaders()
    {
        var services = new ServiceCollection();
        services.AddApcoaFlowApiClient();
        using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IHttpClientFactory>();

        using var login = factory.CreateClient(ServiceCollectionExtensions.LoginClientName);
        await Assert.That(login.BaseAddress).IsEqualTo(new Uri(ApcoaFlowClientOptions.BaseUrl));
        foreach (var name in ApcoaFlowClientOptions.AppHeaders.Keys)
            await Assert.That(login.DefaultRequestHeaders.Contains(name)).IsTrue();
    }
}
