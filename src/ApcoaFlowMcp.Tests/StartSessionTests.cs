using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Models.Promotion;
using ApcoaFlowApiClient.Models.Session;
using ApcoaFlowApiClient.Models.Tariff;
using ApcoaFlowApiClient.ParkingData;
using ApcoaFlowMcp.Apcoa;
using NSubstitute;
using Refit;

namespace ApcoaFlowMcp.Tests;

/// <summary>
/// Unit tests for <see cref="ApcoaFlowSessionService.StartAsync"/> with a fully mocked API. Covers the
/// free/paid decision, the paid-confirmation handshake, promo validation fallback, and the
/// already-active (HTTP 424) handling. No network, no real sessions.
/// </summary>
public class StartSessionTests
{
    private const decimal MaxCost = 20m;

    private static ApcoaFlowSessionService Service(IApcoaFlowApi api) =>
        new(api, Substitute.For<IApcoaParkingDataSource>());

    private static Task<ApcoaFlowSessionService.StartResult> StartNormal(
        IApcoaFlowApi api, int? duration = null, decimal? confirmed = null, bool bypassCap = false) =>
        Service(api).StartAsync(Fake.ParkingId, Fake.Plate, duration,
            ApcoaFlowSessionService.StartMode.Normal, confirmed, MaxCost, bypassCap, CancellationToken.None);

    private static Task<ApcoaFlowSessionService.StartResult> StartOnlyFree(
        IApcoaFlowApi api, int? duration = null) =>
        Service(api).StartAsync(Fake.ParkingId, Fake.Plate, duration,
            ApcoaFlowSessionService.StartMode.OnlyFree, null, MaxCost, false, CancellationToken.None);

    // ─── Free path ───────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task FreeParking_StartsImmediately()
    {
        var api = Fake.Api(quote: Fake.Quote(0m));
        var result = await StartNormal(api);

        await Assert.That(result.Started).IsTrue();
        await Assert.That(result.CostPln).IsEqualTo(0m);
        await Assert.That(result.ParkingId).IsEqualTo(Fake.ParkingId);
        await Assert.That(result.ParkingName).IsEqualTo(Fake.ParkingName);
        await api.Received(1).StartParkingSessionAsync(Arg.Any<StartParkingSessionRequest>(), Arg.Any<CancellationToken>());
    }

    // ─── Paid path: two-step confirmation ──────────────────────────────────────────────────────────

    [Test]
    public async Task PaidParking_FirstCall_ReturnsQuote_DoesNotStart()
    {
        var api = Fake.Api(quote: Fake.Quote(5m));
        var result = await StartNormal(api);

        await Assert.That(result.Started).IsFalse();
        await Assert.That(result.CostPln).IsEqualTo(5m);
        await Assert.That(result.Message).Contains("5.00");
        await api.DidNotReceive().StartParkingSessionAsync(Arg.Any<StartParkingSessionRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PaidParking_ConfirmedMatchingCost_Starts()
    {
        var api = Fake.Api(quote: Fake.Quote(5m));
        var result = await StartNormal(api, confirmed: 5m);

        await Assert.That(result.Started).IsTrue();
        await api.Received(1).StartParkingSessionAsync(Arg.Any<StartParkingSessionRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PaidParking_ConfirmedMismatchedCost_Refuses()
    {
        var api = Fake.Api(quote: Fake.Quote(5m));
        var result = await StartNormal(api, confirmed: 4m);

        await Assert.That(result.Started).IsFalse();
        await Assert.That(result.Message).Contains("does not match");
        await api.DidNotReceive().StartParkingSessionAsync(Arg.Any<StartParkingSessionRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PaidParking_OverCap_RefusesEvenWhenConfirmed()
    {
        var api = Fake.Api(quote: Fake.Quote(50m));
        var result = await StartNormal(api, confirmed: 50m);

        await Assert.That(result.Started).IsFalse();
        await Assert.That(result.Message).Contains("exceeds the limit");
        await api.DidNotReceive().StartParkingSessionAsync(Arg.Any<StartParkingSessionRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PaidParking_OverCap_BypassCap_Starts()
    {
        var api = Fake.Api(quote: Fake.Quote(50m));
        var result = await StartNormal(api, confirmed: 50m, bypassCap: true);

        await Assert.That(result.Started).IsTrue();
    }

    // ─── OnlyFree mode ─────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task OnlyFree_PaidParking_Refuses()
    {
        // No promo offered → OnlyFree can't even proceed to a quote; it refuses up front.
        var api = Fake.Api(quote: Fake.Quote(3m));
        var result = await StartOnlyFree(api);

        await Assert.That(result.Started).IsFalse();
        await Assert.That(result.Message).Contains("No active free-parking promotion");
        await api.DidNotReceive().StartParkingSessionAsync(Arg.Any<StartParkingSessionRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnlyFree_PromoPresentButNotActuallyFree_Refuses()
    {
        // A promo is offered (so OnlyFree proceeds) and validates, but the quote still isn't free.
        var api = Fake.Api(quote: Fake.Quote(3m), offers: Fake.Offers(Fake.FreeTimePromo()));
        var result = await StartOnlyFree(api);

        await Assert.That(result.Started).IsFalse();
        await Assert.That(result.Message).Contains("not free");
        await api.DidNotReceive().StartParkingSessionAsync(Arg.Any<StartParkingSessionRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnlyFree_FreeParking_Starts()
    {
        var api = Fake.Api(quote: Fake.Quote(0m), offers: Fake.Offers(Fake.FreeTimePromo()));
        var result = await StartOnlyFree(api);

        await Assert.That(result.Started).IsTrue();
    }

    // ─── Account/resolution refusals ───────────────────────────────────────────────────────────────

    [Test]
    public async Task UnknownPlate_Refuses()
    {
        var api = Fake.Api(consumer: Fake.Consumer(plate: "XYZ 999"));
        var result = await StartNormal(api);

        await Assert.That(result.Started).IsFalse();
        await Assert.That(result.Message).Contains("not registered");
        await api.DidNotReceive().StartParkingSessionAsync(Arg.Any<StartParkingSessionRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoBookableLot_Refuses()
    {
        var api = Fake.Api(lots: []);
        var result = await StartNormal(api);

        await Assert.That(result.Started).IsFalse();
        await Assert.That(result.Message).Contains("Could not resolve");
    }

    // ─── Promotion validation fallback ──────────────────────────────────────────────────────────────

    [Test]
    public async Task PromoRejectedByValidate_DropsPromo_AndChargesBasePrice()
    {
        // Promo exists and the breakdown WITH a promo quotes free, but validate rejects it, so the
        // service must drop the promo and re-quote — the base price is what matters, not the promo quote.
        var api = Fake.Api(offers: Fake.Offers(Fake.FreeTimePromo()));
        api.ValidatePromotionAsync(Arg.Any<ValidatePromotionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Fake.ValidRejected());
        // Any quote (promo dropped) returns the real paid base price.
        api.GetTariffBreakdownAsync(Arg.Any<TariffBreakdownQuery>(), Arg.Any<CancellationToken>())
            .Returns(Fake.Quote(3m));

        var result = await StartOnlyFree(api);

        await Assert.That(result.Started).IsFalse();
        await Assert.That(result.Message).Contains("not free");
        await api.DidNotReceive().StartParkingSessionAsync(Arg.Any<StartParkingSessionRequest>(), Arg.Any<CancellationToken>());
    }

    // ─── Already-active (HTTP 424 LIMIT_EXCEEDED) handling ─────────────────────────────────────────

    [Test]
    public async Task AlreadyActive_SameParking_ReportsItAndOffersToKeepOrEnd()
    {
        var api = Fake.Api(quote: Fake.Quote(0m));
        api.StartParkingSessionAsync(Arg.Any<StartParkingSessionRequest>(), Arg.Any<CancellationToken>())
            .Returns<StartSessionResponse>(_ => throw Duplicate424());
        api.GetParkingSessionsAsync(Arg.Any<ParkingSessionV2Query>(), Arg.Any<CancellationToken>())
            .Returns([Fake.ActiveSession(777, Fake.ParkingId, Fake.ParkingName, minutesLeft: 25)]);

        var result = await StartNormal(api);

        await Assert.That(result.Started).IsFalse();
        await Assert.That(result.Message).Contains("THIS parking");
        await Assert.That(result.Message).Contains("end_session(777)");
        await Assert.That(result.Message).Contains("min left");
        await Assert.That(result.ExistingSession!.Id).IsEqualTo(777L);
    }

    [Test]
    public async Task AlreadyActive_Elsewhere_ReportsOtherParking()
    {
        var api = Fake.Api(quote: Fake.Quote(0m));
        api.StartParkingSessionAsync(Arg.Any<StartParkingSessionRequest>(), Arg.Any<CancellationToken>())
            .Returns<StartSessionResponse>(_ => throw Duplicate424());
        api.GetParkingSessionsAsync(Arg.Any<ParkingSessionV2Query>(), Arg.Any<CancellationToken>())
            .Returns([Fake.ActiveSession(888, 9999, "Somewhere Else", minutesLeft: 40)]);

        var result = await StartNormal(api);

        await Assert.That(result.Started).IsFalse();
        await Assert.That(result.Message).Contains("ELSEWHERE");
        await Assert.That(result.Message).Contains("Somewhere Else");
        await Assert.That(result.Message).Contains("end_session(888)");
        await Assert.That(result.ExistingSession!.Id).IsEqualTo(888L);
    }

    [Test]
    public async Task AlreadyActive_ButNoneFound_RefusesGracefully()
    {
        var api = Fake.Api(quote: Fake.Quote(0m));
        api.StartParkingSessionAsync(Arg.Any<StartParkingSessionRequest>(), Arg.Any<CancellationToken>())
            .Returns<StartSessionResponse>(_ => throw Duplicate424());
        api.GetParkingSessionsAsync(Arg.Any<ParkingSessionV2Query>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await StartNormal(api);

        await Assert.That(result.Started).IsFalse();
        await Assert.That(result.Message).Contains("LIMIT_EXCEEDED");
    }

    private static ApiException Duplicate424()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://x/parkingSession");
        var response = new HttpResponseMessage((System.Net.HttpStatusCode)424) { RequestMessage = request };
        return ApiException.Create(request, HttpMethod.Post, response, new RefitSettings()).Result;
    }
}
