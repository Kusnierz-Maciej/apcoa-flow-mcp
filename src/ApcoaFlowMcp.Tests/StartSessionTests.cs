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
        // No promo offered → OnlyFree can't be free; it refuses but now returns pricing + points to
        // the paid tool so the LLM can offer it.
        var api = Fake.Api(quote: Fake.Quote(3m));
        var result = await StartOnlyFree(api);

        await Assert.That(result.Started).IsFalse();
        await Assert.That(result.Message).Contains("No free parking is available");
        await Assert.That(result.Message).Contains("start_session");
        await Assert.That(result.PriceLadder.Count).IsGreaterThan(0);
        await api.DidNotReceive().StartParkingSessionAsync(Arg.Any<StartParkingSessionRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnlyFree_PromoPresentButNotActuallyFree_Refuses()
    {
        // A promo is offered (so OnlyFree proceeds) and validates, but the quote still isn't free.
        var api = Fake.Api(quote: Fake.Quote(3m), offers: Fake.Offers(Fake.FreeTimePromo()));
        var result = await StartOnlyFree(api);

        await Assert.That(result.Started).IsFalse();
        await Assert.That(result.Message).Contains("No free parking is available");
        await Assert.That(result.Message).Contains("start_session");
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
        await Assert.That(result.Message).Contains("No free parking is available");
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

    // ─── Price ladder, duration-inflation regression, cancel/refund note ───────────────────────────

    /// <summary>
    /// Wires GetTariffBreakdownAsync to return a price that depends on the requested duration, using
    /// the real Carrefour tier shape: ≤60 min = 2 PLN, 61–120 = 6 PLN, then +4 PLN per further hour.
    /// </summary>
    private static void StubTieredPricing(IApcoaFlowApi api)
    {
        api.GetTariffBreakdownAsync(Arg.Any<TariffBreakdownQuery>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var q = ci.Arg<TariffBreakdownQuery>();
                var minutes = q?.Duration ?? 0;
                var blocks = Math.Max(1, (int)Math.Ceiling(minutes / 60.0));
                var pln = 2m + (blocks - 1) * 4m; // 60→2, 120→6, 180→10, 240→14
                return Fake.Quote(pln);
            });
    }

    [Test]
    public async Task PaidParking_NoDuration_ReturnsShortestTier_AndLadder()
    {
        var api = Fake.Api();
        StubTieredPricing(api);

        var result = await StartNormal(api); // no duration, no confirmation

        await Assert.That(result.Started).IsFalse();
        await Assert.That(result.PriceLadder.Count).IsGreaterThan(1);
        // Shortest tier leads: 60 min = 2 PLN (from the Carrefour-like grid: FLAT 60 min block).
        await Assert.That(result.PriceLadder[0].Minutes).IsEqualTo(60);
        await Assert.That(result.PriceLadder[0].Pln).IsEqualTo(2m);
        await Assert.That(result.Message).Contains("Shortest option: 60 min = 2.00 PLN");
        await Assert.That(result.Message).Contains("durationMinutes=60");
        await Assert.That(result.Message).Contains("Other options");
        await api.DidNotReceive().StartParkingSessionAsync(Arg.Any<StartParkingSessionRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PaidParking_ConfirmChosenDurationAndCost_Starts()
    {
        var api = Fake.Api();
        StubTieredPricing(api);

        // Confirm the 120-min tier at its exact price (6 PLN).
        var result = await StartNormal(api, duration: 120, confirmed: 6m);

        await Assert.That(result.Started).IsTrue();
        await Assert.That(result.DurationMinutes).IsEqualTo(120);
        await api.Received(1).StartParkingSessionAsync(Arg.Any<StartParkingSessionRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidPromo_ShortRequest_IsNotInflated_ToPromoWindow()
    {
        // Carrefour regression: a 120-min free promo is offered but validate REJECTS it. A 60-min
        // request must stay 60 min at the base price (2 PLN), NOT be inflated to the 120-min window
        // (which would quote the 6 PLN tier).
        var api = Fake.Api(offers: Fake.Offers(Fake.FreeTimePromo(freeMinutes: 120)));
        api.ValidatePromotionAsync(Arg.Any<ValidatePromotionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Fake.ValidRejected());
        StubTieredPricing(api);

        var result = await StartNormal(api, duration: 60); // first call, unconfirmed

        await Assert.That(result.Started).IsFalse();
        await Assert.That(result.DurationMinutes).IsEqualTo(60);
        await Assert.That(result.CostPln).IsEqualTo(2m);
        await Assert.That(result.PromotionName).IsNull();
    }

    [Test]
    public async Task CancelRefundNote_NoRefund_WhenLotForbidsRefund()
    {
        // Default Fake lot: enableStopButton=1, allowRefund=0 → "no refund" note on a free start.
        var api = Fake.Api(quote: Fake.Quote(0m), offers: Fake.Offers(Fake.FreeTimePromo()));
        var result = await StartOnlyFree(api);

        await Assert.That(result.Started).IsTrue();
        await Assert.That(result.CancelRefundNote!).Contains("NO refund");
    }

    [Test]
    public async Task CancelRefundNote_ProratedRefund_WhenLotAndTariffAllow()
    {
        var api = Fake.Api(quote: Fake.Quote(0m), offers: Fake.Offers(Fake.FreeTimePromo()),
            lots: Fake.Lots(allowRefund: 1));
        api.GetTariffAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Fake.Tariff(isProrationAllowed: 1));

        var result = await StartOnlyFree(api);

        await Assert.That(result.Started).IsTrue();
        await Assert.That(result.CancelRefundNote!).Contains("prorated");
    }
}
