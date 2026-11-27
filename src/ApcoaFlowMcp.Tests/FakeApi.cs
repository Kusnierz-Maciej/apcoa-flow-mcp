using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Models;
using ApcoaFlowMcp.Apcoa;
using ApcoaFlowMcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Refit;

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
        var pricing = new Pricing(api, time);
        return new ApcoaFlowTools(api, new ParkingFinder(api), pricing, new Sessions(api, pricing, time, NullLogger<Sessions>.Instance),
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

    public const string CardId = "card-visa";
    public const string ParkingId = "c9a5";
    public const string ParkingName = "Targowiska Miejskie Ostrów Wlkp. III";

    /// <summary>A parking, the default saved card, no running session, and quotes from <paramref name="price"/>.</summary>
    public static IApcoaFlowApi ForStarts(Func<int, decimal> price, string prepaymentMode = "autoCharge", PaymentMethod? defaultMethod = null)
    {
        var api = Create();
        api.GetSpaceAsync(ParkingId, Arg.Any<CancellationToken>()).Returns(new SpaceDetails { Id = ParkingId, Name = ParkingName });
        api.GetPaymentMethodsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(
        [
            defaultMethod ?? new PaymentMethod { Id = CardId, Type = "visa", Identifier = "****1234", IsDefault = true, Flow = "saved", RequiredAction = "none", Availability = "available" },
            new PaymentMethod { Id = "hosted-checkout", Type = "unknown", Flow = "hostedCheckout", RequiredAction = "none", Availability = "available" },
        ]);
        // No session before the start; the new one is listed after it.
        api.GetSessionsAsync(Arg.Any<CancellationToken>()).Returns([], [StartedSession()]);
        Quotes(api, price, prepaymentMode);
        api.StartParkingAsync(Arg.Any<StartParkingRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new StartParkingResponse { Id = "new-session" });
        api.UpdateParkingDurationAsync(Arg.Any<UpdateParkingDurationRequest>(), Arg.Any<CancellationToken>())
            .Returns(ExtendAnswer(null));
        return api;
    }

    public static Session StartedSession(string id = "new-session") => new()
    {
        Id = id, Status = "active", StartDate = Now, EndDate = Now.AddMinutes(30), SessionCanBeStopped = true, SessionCanBeExtended = true,
        Space = new SessionSpace { Id = ParkingId, Name = ParkingName }, Vehicle = new SessionVehicle { Id = VehicleId, LicensePlate = LicensePlate },
    };

    /// <summary>An <c>updateParkingDuration</c> answer: 200 with <paramref name="body"/> (null = the empty body free extensions get).</summary>
    public static IApiResponse<UpdateParkingDurationResponse> ExtendAnswer(UpdateParkingDurationResponse? body) =>
        new ApiResponse<UpdateParkingDurationResponse>(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/api/v1/sessions/updateParkingDuration"),
        }, body, new RefitSettings());

    /// <summary>A fixed clock whose delays (polling waits) fire at once, so tests don't sleep.</summary>
    public sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NoTimer();
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
