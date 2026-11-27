using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Auth;
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
