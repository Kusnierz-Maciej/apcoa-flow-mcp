using ApcoaFlowApiClient.Models.Promotion;
using ApcoaFlowApiClient.Models.Session;
using ApcoaFlowApiClient.Models.Tariff;
using ApcoaFlowApiClient.ParkingData;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Refit;

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
[NotInParallel(LiveApi.Serial)]
public class LiveApiSessionTests
{
    // Search origin — Tarnowskie Góry; parking 616319 is a base-tariff, genuinely-free (0.00 PLN),
    // 24/7 street parking. SAFETY: promo-based "free" parkings (e.g. FREE90PL1 in Rzeszów) quote as
    // free but the real start still CHARGES (observed 3.00 PLN), so the tests here deliberately search
    // this area and only accept base-tariff free parkings — never a promo — to stay truly free.
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

    private static readonly string LogFile = Path.Combine(Path.GetTempPath(), "apcoa-session-test.log");

    /// <summary>Step/decision logging for the live tests — written to console and a temp file.</summary>
    private static void Log(string message)
    {
        var line = $"[session-test] {message}";
        Console.WriteLine(line);
        File.AppendAllText(LogFile, line + Environment.NewLine);
    }

    /// <summary>
    /// Crash recovery, once per run: end any session left ACTIVE by a previous run. The account allows only
    /// one active session at a time, so a leftover would make a fresh start fail with 424 LIMIT_EXCEEDED.
    /// </summary>
    [Before(HookType.Class)]
    public static Task SweepBefore() => LiveApi.SweepOnceAtStartAsync();

    /// <summary>
    /// Safety net: after EVERY test in this class, end any session left ACTIVE. This runs even if a
    /// test throws before its own cleanup, so a session-creating test can never leave a billable
    /// session running. Best-effort — failures here are logged, not thrown.
    /// </summary>
    [After(Test)]
    public Task EndAnyActiveSession() => LiveApi.EndActiveSessionsAsync("cleanup");

    /// <summary>A parking that can host a fully-free start + extension, with the IDs needed to do so.</summary>
    private sealed record FreeCandidate(
        int ParkingId, int LotId, int TariffId, TimeSpan Start, TimeSpan Extend, Offer? Promo);

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // DEBUG-ONLY diagnostic probes (compiled out). Flip the #if to `true` to re-enable them when you
    // need to inspect live API behaviour by hand — they only read, never start/stop/charge anything.
    //
    //   DumpRecentSessions_ReadOnly       — lists recent ACTIVE/COMPLETED/IN_REVIEW sessions with full
    //                                        cost + transaction-status detail (use it to confirm whether
    //                                        a start actually charged money or failed).
    //   InvestigatePaidPromoParking_ReadOnly — dumps lots/tariff/offers + validate-promotion + tariff
    //                                        breakdowns (with vs. without promo) for a parking. It was
    //                                        used to prove that Lidl Rzeszów (6024) quotes its FREE90PL1
    //                                        promo as 0.00 via tariffBreakDownV2, yet promotion/validate
    //                                        returns 422 INVALID_PROMOTION_CODE/5007 and the real start
    //                                        charges 3.00 PLN — i.e. the breakdown lies about promos.
    //                                        Change the `ParkingId` constant to probe a different parking.
    // ─────────────────────────────────────────────────────────────────────────────────────────────
#if false
    /// <summary>
    /// READ-ONLY diagnostic: dumps recent COMPLETED and ACTIVE sessions with full cost/status detail so
    /// we can see whether recent starts actually charged money or failed. Starts/stops nothing.
    /// </summary>
    [Test]
    public async Task DumpRecentSessions_ReadOnly()
    {
        using var sp = BuildProvider();
        var api = sp.GetRequiredService<IApcoaFlowApi>();

        foreach (var status in new[] { "ACTIVE", "COMPLETED", "IN_REVIEW" })
        {
            var list = await api.GetParkingSessionsAsync(
                new ParkingSessionV2Query { Status = status, PageSize = 15 });
            Log($"=== {status}: {list.Count} session(s) ===");
            foreach (var s in list)
            {
                Log($"  id={s.Id} '{s.ParkingName}' [{s.ParkingId}] plate={s.LicensePlate} " +
                    $"status={s.ParkingSessionStatus} amount={s.Amount:0.00} {s.CurrencyCode} " +
                    $"checkin={s.CheckinTime:u} checkout={s.CheckoutTime:u} schedOut={s.ScheduledCheckoutTime:u}");
                foreach (var d in s.ParkingSessionDetailList)
                    Log($"      detail type={d.ParkingSessionDetailType} amount={d.Amount:0.00} " +
                        $"tx={d.TransactionStatus} pay={d.PaymentMethodType} card={d.CardType} " +
                        $"{d.StartTime:u}→{d.EndTime:u}");
            }
        }
    }

    /// <summary>
    /// READ-ONLY investigation of the Rzeszów Lidl parking (6024) that CHARGED 3.00 PLN while its
    /// FREE90PL1 promo quoted free. Dumps parking detail, lots, tariff, applicable offers, and tariff
    /// breakdowns both WITH and WITHOUT the promo across several durations — to see where the quoted
    /// "free" and the real charge diverge. Starts/stops nothing.
    /// </summary>
    [Test]
    public async Task InvestigatePaidPromoParking_ReadOnly()
    {
        const int ParkingId = 6024;

        using var sp = BuildProvider();
        var api = sp.GetRequiredService<IApcoaFlowApi>();
        var consumer = await api.GetConsumerAsync();
        var plate = LiveApi.FirstRegisteredCar(consumer).VehicleNumber;

        // Lots + tariff for the parking.
        var lots = await api.GetParkingLotsAsync(ParkingId);
        var lot = lots.FirstOrDefault(l => l.LotId is not null && l.PrimaryTariffId is not null);
        if (lot?.LotId is null || lot.PrimaryTariffId is null)
        {
            Log($"parking {ParkingId}: no bookable lot/tariff.");
            return;
        }
        var lotId = lot.LotId.Value;
        var tariffId = lot.PrimaryTariffId.Value;
        var cfg = lot.ParkingLotConfigurationResponse?.ParkingLotConfigurationList.FirstOrDefault();
        Log($"parking {ParkingId}: lot={lotId} tariff={tariffId} name='{lot.NearbyParkingLot?.Name}' " +
            $"min={cfg?.MinDuration} ext={cfg?.MinExtension} max={lot.MaxDuration}.");

        // Applicable offers (promotions) for this lot.
        var offers = await api.GetApplicableOffersV2Async(new ApplicableOffersV2Request
        {
            ParkingLot = lotId, UserId = consumer.Id, Company = "PL",
        });
        Log($"offers: {offers.Entities.Count} entit(y/ies).");
        foreach (var o in offers.Entities)
            Log($"  offer id={o.Id} name='{o.Name}' active={o.IsActive} autoApply={o.AutoApply} " +
                $"concession={o.ConcessionType} value={o.ConcessionValue} freeWindow={o.FreeWindow} " +
                $"discountType={o.OfferDiscountType} starts={o.OfferStartsAt:u} expires={o.OfferExpiresAt:u}.");

        var promo = offers.Entities.FirstOrDefault(o => o.IsActiveFreeTime);
        var now = DateTimeOffset.UtcNow;

        // Dump the RAW validate-promotion response — this is the authoritative "can this coupon be used
        // right now" check the app runs before starting; its body may reveal usage/consumption state
        // (e.g. a used-up coupon) that the offers list does not expose.
        if (promo is not null)
        {
            var validate = await api.ValidatePromotionAsync(new ValidatePromotionRequest
            {
                ConsumerID = consumer.Id,
                VehicleNumber = plate,
                PromotionName = promo.Name ?? string.Empty,
                Company = "PL",
                ParkingLot = lotId,
                StartTime = now,
                DurationValue = TimeSpan.FromMinutes(90),
            });
            Log($"validate-promotion '{promo.Name}': success={validate.IsSuccessStatusCode} " +
                $"http={validate.StatusCode} message={validate.Content?.Message} " +
                $"statusCode={validate.Content?.StatusCode} description={validate.Content?.Description} " +
                $"errorBody={(validate.Error as Refit.ApiException)?.Content}.");
        }

        // Breakdown WITHOUT promo, then WITH promo, at the free window and a couple of other durations.
        var windows = new[] { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(90) };
        foreach (var w in windows)
        {
            var noPromo = await QuoteAsync(api,
                new FreeCandidate(ParkingId, lotId, tariffId, w, TimeSpan.Zero, Promo: null),
                w, now, plate, consumer.Id);
            Log($"breakdown {w.TotalMinutes:0} min, NO promo: total={noPromo.TotalPayablePln:0.00} {noPromo.CurrencyCode} " +
                $"(cents={noPromo.TotalPayableAmountInCents}, noDiscCents={noPromo.TotalPayableAmountInCentsWithoutDiscount}, " +
                $"discApplicable={noPromo.DiscountApplicable}, discAmt={noPromo.TotalDiscountAmount}, " +
                $"freeApplied={noPromo.FreeDurationApplied}, freeDur={noPromo.FreeDuration}).");

            if (promo is not null)
            {
                var withPromo = await QuoteAsync(api,
                    new FreeCandidate(ParkingId, lotId, tariffId, w, TimeSpan.Zero, promo),
                    w, now, plate, consumer.Id);
                Log($"breakdown {w.TotalMinutes:0} min, WITH promo '{promo.Name}': total={withPromo.TotalPayablePln:0.00} {withPromo.CurrencyCode} " +
                    $"(cents={withPromo.TotalPayableAmountInCents}, noDiscCents={withPromo.TotalPayableAmountInCentsWithoutDiscount}, " +
                    $"discApplicable={withPromo.DiscountApplicable}, discAmt={withPromo.TotalDiscountAmount}, " +
                    $"freeApplied={withPromo.FreeDurationApplied}, freeDur={withPromo.FreeDuration}).");
            }
        }

        Log("NOTE: compare the WITH-promo quote (0.00) to the actual COMPLETED charge of 3.00 PLN seen " +
            "in history for session 84240817 — the promo discount is not honoured at real start.");
    }
#endif

    [Test]
    public async Task FreeSession_Discover_Start_List_Extend_End_Lifecycle()
    {
        using var sp = BuildProvider();
        var api = sp.GetRequiredService<IApcoaFlowApi>();
        var data = sp.GetRequiredService<IApcoaParkingDataSource>();

        var consumer = await api.GetConsumerAsync();
        var vehicle = LiveApi.FirstRegisteredCar(consumer);
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
        var start = await LiveApi.StartWhenSlotFreeAsync(api, () => api.StartParkingSessionAsync(new StartParkingSessionRequest
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
        }));
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
    /// Starts a free session, then immediately attempts a second start at the same parking. Logs
    /// the raw API response so we can see the exact error shape returned for a duplicate-session
    /// conflict and confirm the right graceful handling is in place.
    /// </summary>
    [Test]
    public async Task DuplicateSession_AtSameParking_LogsApiResponse()
    {
        using var sp = BuildProvider();
        var api = sp.GetRequiredService<IApcoaFlowApi>();
        var data = sp.GetRequiredService<IApcoaParkingDataSource>();

        var consumer = await api.GetConsumerAsync();
        var vehicle = LiveApi.FirstRegisteredCar(consumer);
        var payment = consumer.ConsumerUserPaymentMethods.First(p => p.IsDefault == 1);

        Log($"finding a free parking to test duplicate-session behaviour…");
        var candidate = await FindFreeCandidateAsync(api, data, consumer.Id, vehicle.VehicleNumber);
        await Assert.That(candidate).IsNotNull()
            .Because("need a free parking to test duplicate starts without spending money");
        var c = candidate!;
        Log($"using parking {c.ParkingId} (lot {c.LotId}, tariff {c.TariffId}).");

        var checkIn = DateTimeOffset.UtcNow;
        var checkOut = checkIn.Add(c.Start);

        // Start the first session.
        StartSessionResponse first;
        try
        {
            first = await LiveApi.StartWhenSlotFreeAsync(api, () => api.StartParkingSessionAsync(new StartParkingSessionRequest
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
            }));
        }
        catch (ApiException ex)
        {
            Assert.Fail($"First session start failed with HTTP {(int)ex.StatusCode}: {ex.Content}");
            return;
        }
        Log($"first start returned: id={first.SessionId}, status={first.ParkingSessionStatus}, txStatus={first.TransactionStatus}.");
        await Assert.That(first.IsActive).IsTrue()
            .Because($"first start should succeed (got status '{first.ParkingSessionStatus}')");

        // Money-guard: the start transaction must not have charged. A promo parking can quote free yet
        // bill on start (observed), so if anything was charged, stop immediately and fail the test.
        if (first.TransactionAmount is { } charged && charged > 0m)
        {
            await api.StopParkingSessionAsync(new StopParkingSessionRequest
            {
                ParkingSessionId = first.SessionId,
                SessionCheckOutTime = DateTimeOffset.UtcNow,
            });
            Assert.Fail($"First start CHARGED {charged:0.00} {first.CurrencyCode} despite a free quote — stopped session {first.SessionId}. This parking is not truly free; do not test against it.");
            return;
        }
        Log($"first session started: id={first.SessionId}, status={first.ParkingSessionStatus}, charged={first.TransactionAmount:0.00}.");

        // Now try to start a second session at the same parking — expect the API to reject it.
        Log("attempting a second start at the same parking (expect conflict)…");
        try
        {
            var checkIn2 = DateTimeOffset.UtcNow;
            var second = await api.StartParkingSessionAsync(new StartParkingSessionRequest
            {
                TariffId = c.TariffId,
                ParkingId = c.ParkingId,
                ParkingLotId = c.LotId,
                VehicleNumber = vehicle.VehicleNumber,
                PaymentMethod = payment.PaymentType ?? "BLIK",
                ConsumerUserPaymentId = payment.Id,
                ConsumerProfileId = consumer.DefaultProfileId ?? consumer.Id,
                SessionCheckInTime = checkIn2,
                ScheduledSessionCheckInTime = checkIn2,
                ScheduledSessionCheckOutTime = checkIn2.Add(c.Start),
                PromotionName = c.Promo?.Name,
                PromotionId = c.Promo?.Id,
                OfferDiscountType = c.Promo is null ? null : "PROMOTION",
                IsAutoApplyPromotion = c.Promo is not null,
            });
            // If the API somehow allows it, log the unexpected success and let cleanup handle it.
            Log($"UNEXPECTED: second session started without error — id={second.SessionId}, status={second.ParkingSessionStatus}.");
            Log("API BEHAVIOUR: duplicate start was silently allowed (or returned an already-active session).");
        }
        catch (ApiException ex)
        {
            string body;
            System.Text.Json.JsonElement? parsed = null;
            try
            {
                parsed = await ex.GetContentAsAsync<System.Text.Json.JsonElement?>();
                body = parsed is { } jsonEl
                    ? System.Text.Json.JsonSerializer.Serialize(jsonEl, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
                    : ex.Content ?? "(no body)";
            }
            catch
            {
                body = ex.Content ?? "(no body)";
            }
            Log($"API rejected duplicate start — HTTP {(int)ex.StatusCode} {ex.StatusCode}.");
            Log($"Response body:\n{body}");

            // Assert the known conflict shape: 424 FailedDependency + LIMIT_EXCEEDED / 5012.
            await Assert.That((int)ex.StatusCode).IsEqualTo(424)
                .Because("the API signals a duplicate-session conflict with HTTP 424 FailedDependency");
            if (parsed is { } el)
            {
                await Assert.That(el.GetProperty("message").GetString()).IsEqualTo("LIMIT_EXCEEDED")
                    .Because("the conflict response carries message=LIMIT_EXCEEDED");
                await Assert.That(el.GetProperty("statusCode").GetInt32()).IsEqualTo(5012)
                    .Because("the conflict response carries the internal statusCode 5012");
            }
        }
        catch (Exception ex)
        {
            Log($"Unexpected exception type {ex.GetType().Name}: {ex.Message}");
        }

        // Active sessions should still show exactly the first one.
        var active = await api.GetParkingSessionsAsync(new ParkingSessionV2Query { Status = "ACTIVE", PageSize = 10 });
        var activeIds = string.Join(", ", active.Select(s => s.Id.ToString()));
        Log($"active sessions after duplicate attempt: [{activeIds}].");
        await Assert.That(active.Any(s => s.Id == first.SessionId)).IsTrue()
            .Because("the original session must still be active after a rejected duplicate");

        // End the session (cleanup is also handled by EndAnyActiveSession but be explicit here).
        await api.StopParkingSessionAsync(new StopParkingSessionRequest
        {
            ParkingSessionId = first.SessionId,
            SessionCheckOutTime = DateTimeOffset.UtcNow,
        });
        Log($"session {first.SessionId} ended.");
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
            catch (Refit.ApiException ex) when (IsSkippableParkingError(ex))
            {
                Log($"skip {who}: parkingLots lookup rejected (HTTP {(int)ex.StatusCode}).");
                continue;
            }

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

            // IMPORTANT: do NOT use promotions here. A free-time promo makes tariffBreakDownV2 quote
            // 0.00, but the real start still CHARGES for these parkings (observed: FREE90PL1 billed
            // 3.00 PLN while quoting free). Only a BASE tariff that is itself free (like 616319) starts
            // at 0.00 reliably — so the money-guard must pass with no promo applied.
            var c = new FreeCandidate(parking.Id, lotId, tariffId, start, extend, Promo: null);
            const string via = "base tariff";

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
            catch (Refit.ApiException ex) when (IsSkippableParkingError(ex))
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

    /// <summary>
    /// A per-parking "skip" is only legitimate for 400/404 — that parking's config/data doesn't suit our
    /// probe. Any other status (401/403/419 auth-or-lockout, 429 throttle, 5xx server) is an account- or
    /// service-wide failure that must abort the whole search, not be swallowed as a skip. Rethrows those.
    /// </summary>
    private static bool IsSkippableParkingError(Refit.ApiException ex)
    {
        var code = (int)ex.StatusCode;
        return code is 400 or 404;
    }
}
