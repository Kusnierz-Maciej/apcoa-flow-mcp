using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Models;
using ApcoaFlowMcp.Apcoa;
using ApcoaFlowMcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace ApcoaFlowMcp.Tests;

/// <summary>A substitute <see cref="IApcoaFlowApi"/> with one default vehicle on the account, and tools built on it.</summary>
internal static class FakeApi
{
    public const string LicensePlate = "ABC12345";
    public const string VehicleId = "b6e130b7-78d8-41b8-a4fe-667578fe3ff0";
    public const string VehicleDescription = "Test Car";

    /// <summary>Friday 2026-11-27 14:00 UTC (15:00 in Warsaw).</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 27, 14, 0, 0, TimeSpan.Zero);

    public static IApcoaFlowApi Create()
    {
        var api = Substitute.For<IApcoaFlowApi>();
        api.GetVehiclesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new Vehicle { Id = VehicleId, LicensePlate = LicensePlate, Description = VehicleDescription, IsDefault = true },
        ]);
        return api;
    }

    public static ApcoaFlowTools Tools(IApcoaFlowApi api, DateTimeOffset? now = null)
    {
        var time = new FixedTime(now ?? Now);
        return new ApcoaFlowTools(api, new ParkingFinder(api), new Pricing(api, time),
            Options.Create(new ApcoaFlowToolsOptions()), time, NullLogger<ApcoaFlowTools>.Instance);
    }

    /// <summary>Quotes: <paramref name="price"/> maps a duration in minutes to its total in PLN.</summary>
    public static void Quotes(IApcoaFlowApi api, Func<int, decimal> price, string prepaymentMode = "autoCharge") =>
        api.GetPriceAsync(Arg.Any<PriceQuery>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var minutes = (ci.ArgAt<PriceQuery>(0).Duration ?? 0) / 60;
            return new PriceQuote
            {
                TotalPrice = FormattableString.Invariant($"PLN {price(minutes):0.00}"),
                PrepaymentMode = prepaymentMode,
            };
        });

    public sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
