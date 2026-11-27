using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ApcoaFlowApiClient.Tests;

public class RateLimiterTests
{
    [Test]
    public async Task Defaults_Are10Burst_OneSecondPeriod()
    {
        var defaults = new ApcoaFlowRateLimitOptions();
        await Assert.That(defaults.TokenLimit).IsEqualTo(10);
        await Assert.That(defaults.ReplenishmentPeriod).IsEqualTo(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task Burst_UpToTokenLimit_IsImmediate_ThenQueues()
    {
        // Short period so the queued acquire doesn't slow the suite down.
        var options = new ApcoaFlowRateLimitOptions { ReplenishmentPeriod = TimeSpan.FromMilliseconds(200) };
        using var limiter = new ApcoaFlowRateLimiter(Options.Create(options));

        for (var i = 0; i < options.TokenLimit; i++)
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
        await Assert.That(sw.Elapsed).IsLessThan(options.ReplenishmentPeriod * 4);
    }

    [Test]
    public async Task ConfiguredBurst_IsHonoured()
    {
        using var limiter = new ApcoaFlowRateLimiter(Options.Create(new ApcoaFlowRateLimitOptions { TokenLimit = 2 }));
        using var a = limiter.Limiter.AttemptAcquire();
        using var b = limiter.Limiter.AttemptAcquire();
        using var c = limiter.Limiter.AttemptAcquire();
        await Assert.That(b.IsAcquired).IsTrue();
        await Assert.That(c.IsAcquired).IsFalse();
    }

    [Test]
    [Arguments(0, 1000)]
    [Arguments(1001, 1000)]
    [Arguments(10, 0)]
    [Arguments(10, 120_000)]
    public async Task Validator_RejectsNonsense(int tokenLimit, int periodMs) =>
        await Assert.That(new ApcoaFlowRateLimitOptionsValidator().Validate(null, new ApcoaFlowRateLimitOptions
        {
            TokenLimit = tokenLimit, ReplenishmentPeriod = TimeSpan.FromMilliseconds(periodMs),
        }).Failed).IsTrue();

    [Test]
    public async Task Validator_AcceptsDefaults() =>
        await Assert.That(new ApcoaFlowRateLimitOptionsValidator().Validate(null, new ApcoaFlowRateLimitOptions()).Succeeded).IsTrue();

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
