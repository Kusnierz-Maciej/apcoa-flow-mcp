using ApcoaFlowApiClient.ParkingData;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ApcoaFlowApiClient.Tests;

/// <summary>
/// READ-ONLY live checks that APCOA's public endpoints (app config, parking lots, parking detail)
/// still answer without a bearer token. Catches upstream auth changes that would break parking
/// lookup. Pure GETs — never starts, extends or stops a session. Opt-in via the "LiveApi" category.
/// </summary>
[Category("LiveApi")]
[NotInParallel("LiveApi")]
public class LiveApiPublicEndpointTests
{
    private static (IApcoaFlowApi Api, IApcoaParkingDataSource Data) BuildClients()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<LiveApiPublicEndpointTests>(optional: true)
            .AddEnvironmentVariables()
            .Build();
        var options = new ApcoaFlowClientOptions
        {
            Email = config["ApcoaFlow:Email"] ?? throw new InvalidOperationException("ApcoaFlow:Email not set."),
            Password = config["ApcoaFlow:Password"] ?? throw new InvalidOperationException("ApcoaFlow:Password not set."),
        };
        var services = new ServiceCollection();
        services.AddSingleton<IApcoaFlowCredentialProvider>(new StaticProvider(options));
        services.AddApcoaFlowApiClient();
        // Point the snapshot store at a writable temp dir (the default /data/parking is the Docker volume).
        var storeDir = Path.Combine(Path.GetTempPath(), "apcoa-live-test-store");
        services.AddApcoaParkingDataSource(configure: o => o.StorePath = storeDir);
        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<IApcoaFlowApi>(), sp.GetRequiredService<IApcoaParkingDataSource>());
    }

    // App config is a PUBLIC endpoint (419 if a bearer token is sent). The parking data source depends
    // on it for the decryption key, so verify it in isolation first — a failure here must be detected
    // as itself, not masked as a FindNearbyAsync failure in the parking test below.
    [Test]
    public async Task AppConfig_IsPublic_AndReturnsParkingDataKey()
    {
        var (api, _) = BuildClients();
        var config = await api.GetAppConfigAsync(9);
        await Assert.That(config).IsNotNull()
            .Because("app config is a public endpoint and must return without a bearer token (419 if one is sent)");
    }

    // Verifies the public parking-lookup fix without hardcoding an id: discover a real parking near
    // Tarnowskie Góry via the offline index, pick one at random, then call the public parkingLots /
    // detail endpoints on it. They must return 200 (no bearer token).
    // Depends on AppConfig test because FindNearbyAsync needs the app-config decryption key.
    [Test]
    [DependsOn(nameof(AppConfig_IsPublic_AndReturnsParkingDataKey))]
    public async Task PublicParkingEndpoints_SucceedWithoutBearer()
    {
        var (api, data) = BuildClients();

        // Precondition this test depends on: the parking data source needs the app-config key.
        // Assert it directly so a precondition failure is reported here, not masked inside FindNearbyAsync.
        var config = await api.GetAppConfigAsync(9);
        await Assert.That(config).IsNotNull()
            .Because("precondition: FindNearbyAsync needs the app-config decryption key, so app config must be reachable");

        // Tarnowskie Góry town centre; 5 km covers the local parkings.
        var nearby = await data.FindNearbyAsync(50.4453, 18.8617, radiusKm: 5.0);
        await Assert.That(nearby.Count).IsGreaterThan(0)
            .Because("the offline index should return parkings near Tarnowskie Góry");

        var picked = nearby[Random.Shared.Next(nearby.Count)].Parking;

        var lots = await api.GetParkingLotsAsync(picked.Id);
        await Assert.That(lots).IsNotNull()
            .Because($"parkingLots for parking {picked.Id} is public and must return without a bearer token");

        var detail = await api.GetParkingAsync(picked.Id);
        await Assert.That(detail).IsNotNull()
            .Because($"parking detail for {picked.Id} is public and must return without a bearer token");
        await Assert.That(detail.Id).IsEqualTo(picked.Id)
            .Because("the returned detail should be for the requested parking id");
    }

    private sealed class StaticProvider(ApcoaFlowClientOptions options) : IApcoaFlowCredentialProvider
    {
        public ApcoaFlowClientOptions GetCredentials() => options;
    }
}
