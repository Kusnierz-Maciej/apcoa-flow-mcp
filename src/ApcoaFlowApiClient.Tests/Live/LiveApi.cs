using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Auth;
using ApcoaFlowApiClient.Http;
using ApcoaFlowApiClient.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ApcoaFlowApiClient.Tests.Live;

/// <summary>
/// Shared setup and housekeeping for live tests against the real APCOA Flow API with the test account (user-secrets id
/// <c>apcoa-flow-api-tests</c>, or env <c>ApcoaFlow__Email</c> / <c>ApcoaFlow__Password</c>). Every
/// <c>[Category("LiveApi")]</c> class carries <c>[NotInParallel(LiveApi.Serial)]</c> (one account, one rate limit,
/// one active session per plate), calls <see cref="SweepOnceAtStartAsync"/> from <c>[Before(HookType.Class)]</c> and
/// <see cref="EndActiveSessionsAsync"/> from <c>[After(HookType.Class)]</c>.
/// </summary>
internal static class LiveApi
{
    public const string Serial = "LiveApi";

    private static readonly Lazy<(ServiceProvider Services, ApcoaFlowCredentials Credentials)> Instance = new(Build);

    public static IServiceProvider Services => Instance.Value.Services;
    public static IApcoaFlowApi Api => Services.GetRequiredService<IApcoaFlowApi>();
    public static ApcoaFlowCredentials Credentials => Instance.Value.Credentials;

    /// <summary>
    /// The car live tests use: the first registered car, whatever it is. Never hard-code a car id, plate or name.
    /// Each test reads the vehicles itself (no caching: tests must not depend on each other). Fails without a car.
    /// </summary>
    public static Vehicle FirstRegisteredCar(IReadOnlyList<Vehicle> vehicles)
    {
        var car = vehicles.FirstOrDefault();
        if (car is null)
            Assert.Fail("The test account has no registered car; add one in the APCOA Flow app.");
        return car!;
    }

    /// <summary>The reusable free test parking: Targowiska Miejskie Ostrów Wlkp. III (free except Saturday 06–15).</summary>
    public const string Ostrow = "c9a52a81-3c6f-4ddc-9879-21a9ad0e4cc8";

    /// <summary>
    /// The example query for parkings that come with free time (a shop's free 1.5 h, once per 4 h per parking): never
    /// hard-code one, search for them.
    /// </summary>
    public const string FreeTimeQuery = "Biedronka";

    /// <summary>A dated quote for the vehicle, starting now.</summary>
    public static async Task<Money> QuoteAsync(IApcoaFlowApi api, string spaceId, string vehicleId, int minutes) =>
        (await api.GetPriceAsync(new PriceQuery
        {
            SpaceId = spaceId, LicensePlateId = vehicleId, Duration = minutes * 60,
            StartDate = UtcMillisecondsConverter.ToWire(DateTimeOffset.UtcNow),
        })).Total;

    /// <summary>
    /// A parking that is free for <paramref name="minutes"/> for the vehicle right now: Ostrów, else the first
    /// <see cref="FreeTimeQuery"/> hit whose dated quote is 0.00; null when none is.
    /// </summary>
    public static async Task<string?> FindFreeParkingAsync(IApcoaFlowApi api, Vehicle vehicle, int minutes, bool includeOstrow = true)
    {
        if (includeOstrow && (await QuoteAsync(api, Ostrow, vehicle.Id, minutes)).IsZero)
            return Ostrow;
        var hits = (await api.SearchAsync(new SearchQuery { Query = FreeTimeQuery, Latitude = 52.0, Longitude = 19.0, Size = 20, IsParking = true, IsCharging = false })).Spaces;
        foreach (var hit in hits.Take(8))
            if ((await QuoteAsync(api, hit.Id, vehicle.Id, minutes)).IsZero)
                return hit.Id;
        return null;
    }

    private static readonly Lazy<Task> StartSweep = new(() => EndActiveSessionsAsync("start-of-run"));

    /// <summary>Once per run: ends sessions a crashed previous run left running (they can bill).</summary>
    public static Task SweepOnceAtStartAsync() => StartSweep.Value;

    /// <summary>Stops every running session on the test account. Best effort: logs, never throws.</summary>
    public static async Task EndActiveSessionsAsync(string phase)
    {
        try
        {
            foreach (var s in await Api.GetSessionsAsync())
            {
                try
                {
                    await Api.StopParkingAsync(s.Id);
                    Console.Error.WriteLine($"[live] {phase}: ended lingering session {s.Id} at {s.Space?.Name}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[live] {phase}: FAILED to end session {s.Id}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[live] {phase}: could not sweep running sessions: {ex.Message}");
        }
    }

    private sealed class Marker;

    private static (ServiceProvider, ApcoaFlowCredentials) Build()
    {
        var config = new ConfigurationBuilder().AddUserSecrets<Marker>(optional: true).AddEnvironmentVariables().Build();
        var credentials = new ApcoaFlowCredentials(
            config["ApcoaFlow:Email"] ?? throw new InvalidOperationException("ApcoaFlow:Email not configured (user-secrets or ApcoaFlow__Email)."),
            config["ApcoaFlow:Password"] ?? throw new InvalidOperationException("ApcoaFlow:Password not configured (user-secrets or ApcoaFlow__Password)."));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IApcoaFlowCredentialProvider>(new Static(credentials));
        services.AddApcoaFlowApiClient();
        return (services.BuildServiceProvider(), credentials);
    }

    private sealed class Static(ApcoaFlowCredentials c) : IApcoaFlowCredentialProvider
    {
        public ApcoaFlowCredentials GetCredentials() => c;
    }
}
