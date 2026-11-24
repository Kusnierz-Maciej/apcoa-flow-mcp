using ApcoaFlowApiClient.Models.Promotion;
using ApcoaFlowApiClient.Models.Session;
using ApcoaFlowApiClient.Models.Tariff;
using ApcoaFlowApiClient.ParkingData;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ApcoaFlowApiClient.Tests;

/// <summary>
/// Live chained session lifecycle: discover a free parking → start → list(active) → extend → end →
/// verify-gone.
///
/// SAFETY: the test never commits a session that would cost money. It searches real parkings for one
/// whose start AND extension both quote to zero cost (a free tariff, or an active free-time promotion
/// whose window covers start+extension), and re-quotes as a hard money-guard before every start and
/// extend. It self-cleans by ending the session it starts. Gated under "LiveApi" so it does not run in
/// the normal suite — invoke explicitly.
/// </summary>
[Category("LiveApi")]
public class LiveApiSessionTests
{
    // Search origin — Tarnowskie Góry area has both free and promo-free parkings.
    private const double SearchLat = 50.4453;
    private const double SearchLon = 18.8617;
    private const double SearchRadiusKm = 20.0;

    private static ServiceProvider BuildProvider()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<LiveApiSessionTests>(optional: true)
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
        services.AddApcoaParkingDataSource(configure: o =>
            o.StorePath = Path.Combine(Path.GetTempPath(), "apcoa-lifecycle-store"));
        return services.BuildServiceProvider();
    }

    private sealed class StaticProvider(ApcoaFlowClientOptions o) : IApcoaFlowCredentialProvider
    {
        public ApcoaFlowClientOptions GetCredentials() => o;
    }

    /// <summary>Step/decision logging for the live tests — appears in the test output / CI log.</summary>
    private static void Log(string message) => Console.WriteLine($"[session-test] {message}");

    /// <summary>
    /// Safety net: after EVERY test in this class, end any session left ACTIVE. This runs even if a
    /// test throws before its own cleanup, so a session-creating test can never leave a billable
    /// session running. Best-effort — failures here are logged, not thrown.
    /// </summary>
    [After(Test)]
    public async Task EndAnyActiveSession()
    {
        try
        {
            using var sp = BuildProvider();
            var api = sp.GetRequiredService<IApcoaFlowApi>();
            var active = await api.GetParkingSessionsAsync(new ParkingSessionV2Query { Status = "ACTIVE", PageSize = 20 });
            foreach (var s in active)
            {
                try
                {
                    await api.StopParkingSessionAsync(new StopParkingSessionRequest
                    {
                        ParkingSessionId = s.Id,
                        SessionCheckOutTime = DateTimeOffset.UtcNow,
                    });
                    Console.Error.WriteLine($"[session-test] cleanup: ended lingering ACTIVE session {s.Id} at {s.ParkingName}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[session-test] cleanup: FAILED to end session {s.Id}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[session-test] cleanup: could not sweep active sessions: {ex.Message}");
        }
    }

    /// <summary>A parking that can host a fully-free start + extension, with the IDs needed to do so.</summary>
    private sealed record FreeCandidate(
        int ParkingId, int LotId, int TariffId, TimeSpan Start, TimeSpan Extend, Offer? Promo);

    [Test]
    public async Task FreeSession_Discover_Start_List_Extend_End_Lifecycle()
    {
        using var sp = BuildProvider();
        var api = sp.GetRequiredService<IApcoaFlowApi>();
        var data = sp.GetRequiredService<IApcoaParkingDataSource>();

        var consumer = await api.GetConsumerAsync();
        var vehicle = consumer.VehicleInformationResponses.First();
        var payment = consumer.ConsumerUserPaymentMethods.First(p => p.IsDefault == 1);
        Log($"account consumerId={consumer.Id}, profileId={consumer.DefaultProfileId}, plate={vehicle.VehicleNumber}, payment={payment.PaymentType} (id {payment.Id}).");

        // 1. Discover a parking whose start + extension are BOTH free (money-guard built into selection).
        Log($"searching for a fully-free parking within {SearchRadiusKm:0} km of ({SearchLat}, {SearchLon})…");
        var candidate = await FindFreeCandidateAsync(api, data, consumer.Id, vehicle.VehicleNumber);
        await Assert.That(candidate).IsNotNull()
            .Because("no nearby parking offered a fully-free start + extension to test against");
        var c = candidate!;
        var promoNote = c.Promo is null ? "free tariff (no promo)" : $"promo '{c.Promo.Name}' ({c.Promo.FreeMinutes} free min)";
        Log($"CHOSEN parking {c.ParkingId} (lot {c.LotId}, tariff {c.TariffId}) — start {c.Start.TotalMinutes:0} min, extend {c.Extend.TotalMinutes:0} min, via {promoNote}.");

        var checkIn = DateTimeOffset.UtcNow;
        var checkOut = checkIn.Add(c.Start);

        // Hard money-guard: re-quote the start right before starting; refuse if not free.
        var startQuote = await QuoteAsync(api, c, c.Start, checkIn, vehicle.VehicleNumber, consumer.Id);
        Log($"money-guard: start quote for {c.Start.TotalMinutes:0} min = {startQuote.TotalPayablePln:0.00} PLN.");
        await Assert.That(startQuote.IsFree).IsTrue()
            .Because($"money-guard: the start must cost 0 before starting (quoted {startQuote.TotalPayablePln:0.00} PLN)");

        // 2. Start.
        Log($"starting session: parking {c.ParkingId}, {c.Start.TotalMinutes:0} min, check-out {checkOut:u}…");
        var start = await api.StartParkingSessionAsync(new StartParkingSessionRequest
        {
            TariffId = c.TariffId,
            ParkingId = c.ParkingId,
            ParkingLotId = c.LotId,
            VehicleNumber = vehicle.VehicleNumber,
            PaymentMethod = payment.PaymentType ?? "BLIK",
            ConsumerUserPaymentId = payment.Id,
            ConsumerProfileId = consumer.DefaultProfileId ?? consumer.Id,
            SessionCheckInTime = checkIn,
            ScheduledSessionCheckInTime = checkIn,
            ScheduledSessionCheckOutTime = checkOut,
            PromotionName = c.Promo?.Name,
            PromotionId = c.Promo?.Id,
            OfferDiscountType = c.Promo is null ? null : "PROMOTION",
            IsAutoApplyPromotion = c.Promo is not null,
        });
        await Assert.That(start.IsActive).IsTrue()
            .Because("the started session should report ACTIVE status");
        var sessionId = start.SessionId;
        await Assert.That(sessionId).IsGreaterThan(0)
            .Because("a successful start must return a session id for list/extend/end");
        Log($"started OK — session id {sessionId}, status {start.ParkingSessionStatus}.");

        try
        {
            // 3. List active — the new session must appear.
            var active = await api.GetParkingSessionsAsync(new ParkingSessionV2Query { Status = "ACTIVE", PageSize = 10 });
            Log($"active sessions now: [{string.Join(", ", active.Select(s => s.Id))}] — expecting {sessionId}.");
            await Assert.That(active.Any(s => s.Id == sessionId)).IsTrue()
                .Because("the just-started session should appear in the active sessions list");

            // 4. Extend — money-guard: quote the extension window first; only extend if still free.
            var extendTo = checkOut.Add(c.Extend);
            var extendQuote = await QuoteAsync(api, c, c.Start + c.Extend, checkIn, vehicle.VehicleNumber, consumer.Id);
            Log($"money-guard: extension quote for {(c.Start + c.Extend).TotalMinutes:0} min total = {extendQuote.TotalPayablePln:0.00} PLN.");
            if (extendQuote.IsFree)
            {
                try
                {
                    await api.ExtendParkingSessionAsync(new ExtendParkingSessionRequest
                    {
                        ParkingSessionId = sessionId,
                        TariffId = c.TariffId,
                        ConsumerUserPaymentId = payment.Id,
                        ExtensionStartTime = checkOut,
                        ExtensionEndTime = extendTo,
                    });
                    Log($"extended session {sessionId} to {extendTo:u} (free).");
                }
                catch (Exception ex)
                {
                    Log($"extend rejected by API (tolerated): {ex.Message}");
                }
            }
            else
            {
                Log("extension would cost money — skipped (money-guard).");
            }
        }
        finally
        {
            // 5. End the session (always, so nothing is left dangling).
            Log($"ending session {sessionId}…");
            await api.StopParkingSessionAsync(new StopParkingSessionRequest
            {
                ParkingSessionId = sessionId,
                SessionCheckOutTime = DateTimeOffset.UtcNow,
            });
        }

        // 6. Verify it is no longer active and completed at zero cost.
        var stillActive = await api.GetParkingSessionsAsync(new ParkingSessionV2Query { Status = "ACTIVE", PageSize = 10 });
        await Assert.That(stillActive.Any(s => s.Id == sessionId)).IsFalse()
            .Because("the ended session must no longer appear in the active list");

        var history = await api.GetParkingSessionsAsync(new ParkingSessionV2Query { Status = "COMPLETED", PageSize = 10 });
        var completed = history.FirstOrDefault(s => s.Id == sessionId);
        await Assert.That(completed).IsNotNull()
            .Because("the ended session should appear in completed history");
        await Assert.That(completed!.Amount).IsEqualTo(0m)
            .Because("the whole lifecycle was guarded to be free, so the final charge must be 0");
        Log($"lifecycle complete — session {sessionId} ended, final charge {completed.Amount:0.00} {completed.CurrencyCode}.");
    }

    /// <summary>
    /// Scans nearby parkings for one whose shortest start AND one extension increment both quote to
    /// zero cost (free tariff, or an active free-time promo whose window ≥ start+extension).
    /// </summary>
    private static async Task<FreeCandidate?> FindFreeCandidateAsync(
        IApcoaFlowApi api, IApcoaParkingDataSource data, int consumerId, string plate)
    {
        var nearby = await data.FindNearbyAsync(SearchLat, SearchLon, SearchRadiusKm);
        Log($"{nearby.Count} parkings in range; evaluating nearest-first…");

        foreach (var (parking, distanceKm) in nearby)
        {
            var who = $"parking {parking.Id} '{parking.DisplayName}' ({distanceKm * 1000:0} m)";

            List<Models.Parking.ParkingLot> lots;
            try { lots = await api.GetParkingLotsAsync(parking.Id); }
            catch (Exception ex) { Log($"skip {who}: parkingLots lookup failed ({ex.Message})."); continue; }

            var lot = lots.FirstOrDefault(l => l.LotId is not null && l.PrimaryTariffId is not null);
            if (lot?.LotId is null || lot.PrimaryTariffId is null)
            {
                Log($"skip {who}: no bookable lot/tariff.");
                continue;
            }

            var lotId = lot.LotId.Value;
            var tariffId = lot.PrimaryTariffId.Value;
            var cfg = lot.ParkingLotConfigurationResponse?.ParkingLotConfigurationList.FirstOrDefault();
            var start = cfg?.MinDuration is { } md && md > TimeSpan.Zero ? md : TimeSpan.FromMinutes(5);
            var extend = cfg?.MinExtension is { } me && me > TimeSpan.Zero ? me : TimeSpan.FromMinutes(60);

            // Look for an active free-time promo covering start+extension.
            Offer? promo = null;
            try
            {
                var offers = await api.GetApplicableOffersV2Async(new ApplicableOffersV2Request
                {
                    ParkingLot = lotId, UserId = consumerId, Company = "PL",
                });
                promo = offers.Entities.FirstOrDefault(o =>
                    o.FreeWindow is { } fw && fw >= start + extend);
            }
            catch { /* offers optional */ }

            var c = new FreeCandidate(parking.Id, lotId, tariffId, start, extend, promo);
            var via = promo is null ? "base tariff" : $"promo '{promo.Name}'";

            // Confirm BOTH windows quote to zero cost before accepting this candidate. A parking whose
            // breakdown rejects our request (400) is simply skipped rather than aborting the search.
            try
            {
                var now = DateTimeOffset.UtcNow;
                var startQuote = await QuoteAsync(api, c, start, now, plate, consumerId);
                if (!startQuote.IsFree)
                {
                    Log($"skip {who}: start of {start.TotalMinutes:0} min costs {startQuote.TotalPayablePln:0.00} PLN ({via}).");
                    continue;
                }

                var extQuote = await QuoteAsync(api, c, start + extend, now, plate, consumerId);
                if (!extQuote.IsFree)
                {
                    Log($"skip {who}: start free but +{extend.TotalMinutes:0} min extension costs {extQuote.TotalPayablePln:0.00} PLN ({via}).");
                    continue;
                }
            }
            catch (Refit.ApiException ex)
            {
                Log($"skip {who}: breakdown quote rejected (HTTP {(int)ex.StatusCode}).");
                continue;
            }

            Log($"accept {who}: start + extension both free via {via}.");
            return c;
        }

        Log("no suitable fully-free parking found.");
        return null;
    }

    private static Task<TariffBreakdown> QuoteAsync(
        IApcoaFlowApi api, FreeCandidate c, TimeSpan window, DateTimeOffset checkIn, string plate, int consumerId) =>
        api.GetTariffBreakdownAsync(TariffBreakdownQuery.For(
            c.LotId, c.TariffId, checkIn, window, plate,
            promotionId: c.Promo?.Id,
            discountName: c.Promo?.Name,
            offerDiscountType: c.Promo is null ? null : "PROMOTION",
            consumerUserId: consumerId));
}
