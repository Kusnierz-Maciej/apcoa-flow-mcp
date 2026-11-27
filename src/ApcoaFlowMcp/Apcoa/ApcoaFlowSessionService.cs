using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Models.Consumer;
using ApcoaFlowApiClient.Models.Parking;
using ApcoaFlowApiClient.Models.Promotion;
using ApcoaFlowApiClient.Models.Session;
using ApcoaFlowApiClient.Models.Tariff;
using ApcoaFlowApiClient.ParkingData;

namespace ApcoaFlowMcp.Apcoa;

/// <summary>
/// Shared orchestration for the session tools: resolve a parking's lot/tariff, quote a price
/// (optionally with a free-time promotion), enforce the two-step paid confirmation, and start/stop.
/// Pure of MCP concerns so all start/stop tools reuse it.
/// </summary>
public sealed class ApcoaFlowSessionService(
    IApcoaFlowApi api,
    IApcoaParkingDataSource parkingData,
    ILogger<ApcoaFlowSessionService> logger)
{
    private const string Company = "PL";

    /// <summary>
    /// Default session length (minutes) used by the Normal start path when the caller omits a duration.
    /// Compile-time constant (not config) — the single source of truth for the "no duration given" case.
    /// </summary>
    public const int DefaultDurationMinutes = 60;

    /// <summary>Outcome of a start attempt: either started, or a quote that needs confirmation/was refused.</summary>
    public sealed record StartResult(
        bool Started,
        decimal CostPln,
        int DurationMinutes,
        string? PromotionName,
        int? FreeMinutes,
        string Message,
        StartSessionResponse? Session)
    {
        /// <summary>
        /// What happened, the same value as the <c>apcoa.outcome</c> span tag (<c>started</c>, <c>quote</c>,
        /// <c>not_free</c>, <c>cost_cap_exceeded</c>, …) — available for logs even when telemetry is off.
        /// </summary>
        public string Outcome { get; init; } = "unknown";

        /// <summary>The parking the session targets (set once resolved).</summary>
        public int ParkingId { get; init; }
        public string? ParkingName { get; init; }

        /// <summary>Distance from the user to the parking, when started via current location.</summary>
        public int? DistanceMeters { get; init; }

        /// <summary>When non-null, there is already an active session at this parking.</summary>
        public ParkingSessionV2? ExistingSession { get; init; }

        /// <summary>
        /// Price tiers for this lot (shortest first), populated when a start is NOT free so the caller
        /// can pick a duration. Empty when the session is free or no ladder was computed.
        /// </summary>
        public IReadOnlyList<PriceTier> PriceLadder { get; init; } = [];

        /// <summary>Short note on whether the session can be stopped early and whether that refunds.</summary>
        public string? CancelRefundNote { get; init; }

        /// <summary>
        /// When a free-time coupon was consumed by a started session, how long until it can be used
        /// again (its reuse cooldown). Null when no coupon was applied or the coupon has no cooldown.
        /// </summary>
        public TimeSpan? PromotionReuseCooldown { get; init; }
    }

    /// <summary>One price option: a duration and the total cost to park for it.</summary>
    public sealed record PriceTier(int Minutes, decimal Pln);

    public enum StartMode { Normal, OnlyFree }

    /// <summary>
    /// Finds the closest parking within <paramref name="radiusMeters"/> of (lat, lon) and starts a
    /// session there via <see cref="StartAsync"/>. Refuses if no parking is in range.
    /// </summary>
    public async Task<StartResult> StartAtLocationAsync(
        double latitude,
        double longitude,
        string licensePlate,
        int? durationMinutes,
        StartMode mode,
        decimal? confirmedCostPln,
        decimal maxCostPln,
        bool bypassCap,
        double radiusMeters,
        CancellationToken ct)
    {
        IReadOnlyList<(ApcoaFlowApiClient.ParkingData.ParkingEntry Parking, double DistanceKm)> nearby;
        using (ApcoaTelemetry.Phase("apcoa.find_nearest", (ApcoaTelemetry.Tags.RadiusMeters, radiusMeters)))
            nearby = await parkingData.FindNearbyAsync(latitude, longitude, radiusMeters / 1000.0, ct: ct);
        var closest = nearby.FirstOrDefault();
        if (closest.Parking is null)
            return Refused($"No parking found within {radiusMeters:0} m of your location.", "no_parking_nearby");

        var distanceMeters = (int)Math.Round(closest.DistanceKm * 1000);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.DistanceMeters, distanceMeters);
        var result = await StartAsync(closest.Parking.Id, licensePlate, durationMinutes, mode,
            confirmedCostPln, maxCostPln, bypassCap, ct);
        return result with { DistanceMeters = distanceMeters };
    }

    /// <summary>
    /// Resolves IDs, quotes the price (auto-applying a free-time promo when one exists or when
    /// <paramref name="mode"/> is OnlyFree), and either starts (free, or confirmed paid within cap) or
    /// returns a quote for confirmation.
    /// </summary>
    /// <param name="bypassCap">When true, skip the <paramref name="maxCostPln"/> ceiling (expensive tool).</param>
    public async Task<StartResult> StartAsync(
        int parkingId,
        string licensePlate,
        int? durationMinutes,
        StartMode mode,
        decimal? confirmedCostPln,
        decimal maxCostPln,
        bool bypassCap,
        CancellationToken ct)
    {
        logger.LogInformation("StartAsync: parking {ParkingId}, plate {Plate}, mode {Mode}, duration {Duration} min.",
            parkingId, licensePlate, mode, durationMinutes);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ParkingId, parkingId);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.Plate, licensePlate);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.StartMode, mode.ToString());
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.BypassCap, bypassCap);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ConfirmedCostPln, (double?)confirmedCostPln);

        // 1. Resolve lot + tariff from the public parkingLots endpoint.
        List<ParkingLot> lots;
        try
        {
            using (ApcoaTelemetry.Phase("apcoa.resolve_lot"))
                lots = await api.GetParkingLotsAsync(parkingId, ct: ct);
        }
        catch (Refit.ApiException ex)
        {
            logger.LogError(ex, "GetParkingLots failed for parking {ParkingId}: HTTP {Status}.", parkingId, (int)ex.StatusCode);
            throw;
        }
        var lot = lots.FirstOrDefault(l => l.LotId is not null && l.PrimaryTariffId is not null);
        if (lot?.LotId is null || lot.PrimaryTariffId is null)
        {
            logger.LogWarning("No bookable lot/tariff found for parking {ParkingId} ({LotCount} lots returned).", parkingId, lots.Count);
            return Refused($"Could not resolve a bookable lot/tariff for parking {parkingId}.", "no_bookable_lot");
        }

        var lotId = lot.LotId.Value;
        var tariffId = lot.PrimaryTariffId.Value;
        var parkingName = lot.NearbyParkingLot?.Name;
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.LotId, lotId);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.TariffId, tariffId);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ParkingName, parkingName);
        var maxLot = lot.MaxDuration is { } m && m > TimeSpan.Zero ? m : TimeSpan.MaxValue;

        // Stamps parking identity onto any result produced once the parking is resolved.
        StartResult Tag(StartResult r) => r with { ParkingId = parkingId, ParkingName = parkingName };

        // 2. Resolve the consumer: vehicle (by plate or default), default payment method, profile id.
        ConsumerProfile consumer;
        try
        {
            using (ApcoaTelemetry.Phase("apcoa.resolve_consumer"))
                consumer = await api.GetConsumerAsync(ct: ct);
        }
        catch (Refit.ApiException ex)
        {
            logger.LogError(ex, "GetConsumer failed: HTTP {Status}.", (int)ex.StatusCode);
            throw;
        }
        var vehicle = consumer.VehicleInformationResponses
            .FirstOrDefault(v => string.Equals(Normalize(v.VehicleNumber), Normalize(licensePlate), StringComparison.OrdinalIgnoreCase));
        if (vehicle is null)
        {
            logger.LogWarning("Plate '{Plate}' not found on account (account has {Count} vehicle(s)).", licensePlate, consumer.VehicleInformationResponses.Count);
            return Refused($"License plate '{licensePlate}' is not registered on the account. Use get_registered_cars to see available plates.", "plate_not_registered");
        }

        var payment = consumer.ConsumerUserPaymentMethods.FirstOrDefault(p => p.IsDefault == 1)
                      ?? consumer.ConsumerUserPaymentMethods.FirstOrDefault();
        if (payment is null)
            return Refused("No payment method is configured on the account.", "no_payment_method");

        // The session's consumerProfileId is the default profile's id, NOT the top-level consumer id.
        if (consumer.DefaultProfileId is not { } profileId)
            return Refused("No consumer profile is available on the account.", "no_consumer_profile");

        // 3. Find an active free-time promotion for this lot, then VALIDATE it BEFORE using it to
        //    decide the duration. The tariff breakdown applies a promo optimistically even when it is
        //    actually unusable (expired/used-up/on cooldown — e.g. the "every 4h" coupon returns 422
        //    INVALID_PROMOTION_CODE / 5007 and the breakdown then only gives a small % discount, not
        //    the free window). Validating first means an UNUSABLE promo never inflates the requested
        //    duration to its free window, so a 60-min request stays 60 min at the real price instead
        //    of being quoted the (unavailable) 120-min free-window tier.
        //
        //    Endpoint + identity match the official app exactly: v2/applicableOffersWithPromotion keyed
        //    by the PLATE (not the consumer id). Each returned offer carries the real promotion in its
        //    nested promotionResponseList — Offer.PromotionId/PromotionName surface that (the offer's own
        //    id/name are just the display code and must NOT be sent to validate/breakdown/start).
        List<Offer> offers;
        try
        {
            using (ApcoaTelemetry.Phase("apcoa.offers"))
                offers = await api.GetApplicableOffersAsync(new ApplicableOffersRequest
                {
                    ParkingLot = lotId,
                    Identifier = vehicle.VehicleNumber,
                    Company = Company,
                }, ct);
        }
        catch (Refit.ApiException ex)
        {
            logger.LogWarning(ex, "GetApplicableOffers failed for lot {LotId}: HTTP {Status} — proceeding without promotions.", lotId, (int)ex.StatusCode);
            offers = [];
        }
        var promo = offers.FirstOrDefault(o => o.IsActiveFreeTime);
        logger.LogDebug("GetApplicableOffers: {Count} offer(s) for lot {LotId}, free-time promo: {Promo}.",
            offers.Count, lotId, promo?.Name ?? "none");

        var checkIn = DateTimeOffset.UtcNow;
        var requested = durationMinutes is { } dm ? TimeSpan.FromMinutes(dm) : (TimeSpan?)null;

        // Validate against the duration we would actually use with the promo (its free window, capped),
        // which is what the real start would request.
        if (promo?.FreeWindow is { } fw)
        {
            var validateWindow = fw <= maxLot ? fw : maxLot;
            if (!await PromotionValidatesAsync(promo, lotId, vehicle.VehicleNumber, consumer.Id, checkIn, validateWindow, ct))
            {
                logger.LogInformation("Promotion '{Promo}' (lot {Lot}) failed validation — ignoring it.", promo.Name, lotId);
                promo = null;
            }
            else
            {
                logger.LogInformation("Promotion '{Promo}' (lot {Lot}) validated OK, free window {Window} min.", promo.Name, lotId, fw.TotalMinutes);
            }
        }

        // Cancel / refund note for this lot+tariff (so the user knows if stopping early refunds).
        var cancelNote = await BuildCancelRefundNoteAsync(lotId, lot, ct);

        // 4. Decide the duration — only a VALIDATED free-time promo expands the window.
        //    With a usable promo, fill the whole free window (a one-use coupon should not be wasted):
        //    a shorter request rounds UP to it; OnlyFree rejects a longer request. Capped by lot max.
        TimeSpan window;
        if (mode == StartMode.OnlyFree && promo?.FreeWindow is null)
        {
            // No free-time promo: the base tariff itself may be free (e.g. 0.00 PLN street parking). Quote the
            // requested/default window and let the "OnlyFree must actually be free" check below decide — a paid
            // parking is refused there with the price ladder.
            window = requested ?? TimeSpan.FromMinutes(DefaultDurationMinutes);
            window = window <= maxLot ? window : maxLot;
        }
        else if (mode == StartMode.OnlyFree && promo?.FreeWindow is { } promoWindow)
        {
            if (requested is { } r && r > promoWindow)
                return Tag(Refused($"Requested {r.TotalMinutes:0} min exceeds the free window of {promoWindow.TotalMinutes:0} min for this parking.", "exceeds_free_window")) with { CancelRefundNote = cancelNote };

            window = requested is { } rq && rq > promoWindow ? rq : promoWindow;
            window = window <= maxLot ? window : maxLot;
        }
        else
        {
            window = requested ?? TimeSpan.FromMinutes(DefaultDurationMinutes);

            if (promo?.FreeWindow is { } freeWindow && window < freeWindow)
                window = freeWindow;

            window = window <= maxLot ? window : maxLot;
        }

        var checkOut = checkIn.Add(window);

        // 6. Quote the final (promo-applied) price.
        TariffBreakdown quote;
        using (ApcoaTelemetry.Phase("apcoa.quote", (ApcoaTelemetry.Tags.DurationMinutes, (int)window.TotalMinutes)))
            quote = await QuoteAsync(tariffId, lotId, window, checkIn, vehicle.VehicleNumber, promo, consumer.Id, ct);
        var costPln = quote.TotalPayablePln;
        var minutes = (int)window.TotalMinutes;
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.DurationMinutes, minutes);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.CostPln, (double)costPln);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.Promotion, promo?.Name);

        // A note describing the free window applied by a promo, so the paid confirmation makes clear
        // how much of the time is free vs charged (empty when no free-time promo applies).
        var freeNote = promo?.FreeMinutes is { } fm
            ? $" A {fm}-min free promotion ('{promo.Name}') is applied, so you are only charged for the time beyond it."
            : string.Empty;

        // OnlyFree must actually be free (a validated promo can still leave paid overage if the
        // request exceeds the free window).
        if (mode == StartMode.OnlyFree && !quote.IsFree)
        {
            var ladder = await BuildPriceLadderAsync(tariffId, lotId, checkIn, vehicle.VehicleNumber, consumer.Id, maxLot, ct);
            return Tag(Refused(FormatNoFreeMessage(parkingId, ladder), "not_free", isError: false)) with { PriceLadder = ladder, CancelRefundNote = cancelNote };
        }

        // 7. Free → start now. Paid → require confirmation and the cap.
        if (!quote.IsFree)
        {
            // Build the price ladder once, so every paid branch can show the options.
            var ladder = await BuildPriceLadderAsync(tariffId, lotId, checkIn, vehicle.VehicleNumber, consumer.Id, maxLot, ct);

            StartResult PaidQuote(string message, string outcome) =>
                Tag(new StartResult(false, costPln, minutes, promo?.Name, promo?.FreeMinutes, message, null))
                    with { PriceLadder = ladder, CancelRefundNote = cancelNote, Outcome = outcome };

            if (!bypassCap && costPln > maxCostPln)
            {
                ApcoaTelemetry.Fail("cost_cap_exceeded", $"{costPln:0.00} PLN > cap {maxCostPln:0.00} PLN");
                return PaidQuote($"Cost {costPln:0.00} PLN for {minutes} min exceeds the limit of {maxCostPln:0.00} PLN. Use start_expensive_session to override.{freeNote}{FormatLadderSuffix(ladder)}", "cost_cap_exceeded");
            }

            if (confirmedCostPln is null)
            {
                // First step of the two-step paid flow — a normal outcome, not an error.
                ApcoaTelemetry.Outcome("quote");
                // Lead with the SHORTEST startable option, then list the rest, and tell the caller to
                // re-invoke with the chosen durationMinutes + confirmedCostPln.
                var shortest = ladder.Count > 0 ? ladder[0] : new PriceTier(minutes, costPln);
                return PaidQuote(
                    $"This is a PAID session. Shortest option: {shortest.Minutes} min = {shortest.Pln:0.00} PLN. " +
                    $"Re-invoke with durationMinutes={shortest.Minutes} and confirmedCostPln={shortest.Pln:0.00} to start.{freeNote}{FormatLadderSuffix(ladder)}", "quote");
            }

            // The confirmed cost must match the live cost exactly — refuse if it differs either way
            // (price changed, promo expired, or a stale/guessed confirmation).
            if (costPln != confirmedCostPln.Value)
            {
                ApcoaTelemetry.Fail("cost_mismatch", $"live {costPln:0.00} PLN != confirmed {confirmedCostPln.Value:0.00} PLN");
                return PaidQuote($"Live cost {costPln:0.00} PLN for {minutes} min does not match the confirmed {confirmedCostPln.Value:0.00} PLN; not starting. Re-confirm with the exact current cost.{freeNote}{FormatLadderSuffix(ladder)}", "cost_mismatch");
            }
        }

        // 8. Start the session.
        logger.LogInformation("Starting session: parking {ParkingId} lot {LotId}, plate {Plate}, {Minutes} min, cost {Cost} PLN, promo {Promo}.",
            parkingId, lotId, vehicle.VehicleNumber, minutes, costPln, promo?.Name ?? "none");
        StartSessionResponse response;
        try
        {
            using var startPhase = ApcoaTelemetry.Phase("apcoa.start");
            response = await api.StartParkingSessionAsync(new StartParkingSessionRequest
            {
                TariffId = tariffId,
                ParkingId = parkingId,
                ParkingLotId = lotId,
                VehicleNumber = vehicle.VehicleNumber,
                PaymentMethod = payment.PaymentType ?? "BLIK",
                ConsumerUserPaymentId = payment.Id,
                ConsumerProfileId = profileId,
                SessionCheckInTime = checkIn,
                ScheduledSessionCheckInTime = checkIn,
                ScheduledSessionCheckOutTime = checkOut,
                PromotionName = promo?.PromotionName,
                PromotionId = promo?.PromotionId,
                OfferDiscountType = promo is null ? null : "PROMOTION",
                IsAutoApplyPromotion = promo is not null,
            }, ct);
        }
        catch (Refit.ApiException ex) when ((int)ex.StatusCode == 424)
        {
            logger.LogWarning("StartParkingSession returned 424 (LIMIT_EXCEEDED) for parking {ParkingId} — fetching active sessions.", parkingId);
            // The API rejects a start with HTTP 424 FailedDependency / LIMIT_EXCEEDED (statusCode 5012)
            // when a session is already active (one at a time per account). Fetch the active session(s)
            // and tell the user whether one is already running HERE (so they may just use it) or
            // ELSEWHERE (so they can end it and retry), including how long each has left.
            List<ParkingSessionV2> stillActive;
            using (ApcoaTelemetry.Phase("apcoa.active_lookup"))
                stillActive = await api.GetParkingSessionsAsync(
                    new ParkingSessionV2Query { Status = "ACTIVE", PageSize = 10 }, ct);

            ApcoaTelemetry.Fail("session_already_active", "HTTP 424 LIMIT_EXCEEDED");
            if (stillActive.Count == 0)
                return Tag(Refused("The API rejected the start with LIMIT_EXCEEDED, but no active session was found. Try again in a moment, or call get_active_sessions.", "session_already_active"));

            // Prefer a session at the parking the user was trying to start at.
            var sameParking = stillActive.FirstOrDefault(s => s.ParkingId == parkingId);
            var primary = sameParking ?? stillActive[0];

            var sb = new System.Text.StringBuilder();
            if (sameParking is not null)
            {
                sb.Append($"You already have an active session at THIS parking ({DescribeActive(sameParking)}). ");
                sb.Append($"It is already running here — you can keep using it, or end it with end_session({sameParking.Id}) and start a fresh one.");
            }
            else
            {
                sb.Append($"Can't start here — a session is already active ELSEWHERE: {DescribeActive(primary)}. ");
                sb.Append($"Only one session can run at a time. End it with end_session({primary.Id}) and then retry, or keep the current one.");
            }

            // If there is more than one active session, list the rest so the user sees the full picture.
            if (stillActive.Count > 1)
            {
                sb.Append($" Other active session(s): ");
                sb.Append(string.Join("; ", stillActive.Where(s => s.Id != primary.Id).Select(DescribeActive)));
                sb.Append('.');
            }

            return Tag(new StartResult(false, 0m, 0, null, null, sb.ToString(), null)
                { ExistingSession = primary, Outcome = "session_already_active" });
        }
        catch (Refit.ApiException ex)
        {
            logger.LogError(ex, "StartParkingSession failed for parking {ParkingId}: HTTP {Status}.", parkingId, (int)ex.StatusCode);
            throw;
        }

        logger.LogInformation("Session started: id {SessionId}, parking {ParkingId}, plate {Plate}, {Minutes} min, {Kind}.",
            response.SessionId, parkingId, vehicle.VehicleNumber, minutes, quote.IsFree ? "free" : $"{costPln:0.00} PLN");
        var kind = quote.IsFree ? "free" : $"{costPln:0.00} PLN";
        ApcoaTelemetry.Outcome("started");
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.SessionId, response.SessionId);

        // A consumed coupon normally carries a recognised reuse cooldown (e.g. PROMO_MINUTE=240 → 4h). If
        // the API ever sends a cooldown in a unit we don't map, ReuseCooldown is null and we show nothing —
        // log the raw unit as a warning so the new unit can be added to Offer.ReuseCooldown.
        if (promo?.HasUnrecognisedCooldownUnit == true)
            logger.LogWarning(
                "Promotion '{Promotion}' (lot {Lot}) has an unrecognised reuse-cooldown unit '{Unit}' (value {Value}); " +
                "no reuse-cooldown message will be shown. Add this unit to Offer.ReuseCooldown.",
                promo.PromotionName, lotId, promo.ReuseCooldownUnit, promo.PrimaryPromotion?.PromotionDurationUsageTime);

        return Tag(new StartResult(true, costPln, minutes, promo?.Name, promo?.FreeMinutes,
            $"Started a {kind} session for {minutes} min on parking {parkingId}.", response))
            with { CancelRefundNote = cancelNote, PromotionReuseCooldown = promo?.ReuseCooldown, Outcome = "started" };
    }

    /// <summary>Stops a session and returns the final (completed) session with its cost.</summary>
    public async Task<ParkingSessionV2?> StopAsync(long sessionId, CancellationToken ct)
    {
        logger.LogInformation("StopAsync: session {SessionId}.", sessionId);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.SessionId, sessionId);
        try
        {
            using (ApcoaTelemetry.Phase("apcoa.stop"))
                await api.StopParkingSessionAsync(new StopParkingSessionRequest
                {
                    ParkingSessionId = sessionId,
                    SessionCheckOutTime = DateTimeOffset.UtcNow,
                }, ct);
        }
        catch (Refit.ApiException ex)
        {
            logger.LogError(ex, "StopParkingSession failed for session {SessionId}: HTTP {Status}.", sessionId, (int)ex.StatusCode);
            throw;
        }

        List<ParkingSessionV2> completed;
        using (ApcoaTelemetry.Phase("apcoa.reread_completed"))
            completed = await api.GetParkingSessionsAsync(new ParkingSessionV2Query { Status = "COMPLETED", PageSize = 10 }, ct);
        var result = completed.FirstOrDefault(s => s.Id == sessionId);
        ApcoaTelemetry.Outcome("stopped");
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.CostPln, (double?)result?.Amount);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ParkingName, result?.ParkingName);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.Plate, result?.LicensePlate);
        logger.LogInformation("StopAsync: session {SessionId} stopped, final cost {Cost} {Currency}.",
            sessionId, result?.Amount, result?.CurrencyCode ?? "?");
        return result;
    }

    public Task<List<ParkingSessionV2>> GetActiveAsync(CancellationToken ct) =>
        api.GetParkingSessionsAsync(new ParkingSessionV2Query { Status = "ACTIVE", PageSize = 10 }, ct);

    public Task<List<ParkingSessionV2>> GetHistoryAsync(CancellationToken ct) =>
        api.GetParkingSessionsAsync(new ParkingSessionV2Query { Status = "COMPLETED", PageSize = 10 }, ct);

    public async Task<List<Vehicle>> GetVehiclesAsync(CancellationToken ct) =>
        (await api.GetConsumerAsync(ct: ct)).VehicleInformationResponses;

    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds the price ladder (shortest first) for a lot by quoting a set of candidate durations with
    /// NO promotion and collapsing consecutive equal prices to the shortest duration at that price. The
    /// candidate durations come from the lot's own tariff grid when available (block boundaries), else a
    /// default ladder. Everything is capped at <paramref name="maxLot"/>. A duration that quotes to 0 is
    /// kept (it signals a free tier). Any quote that throws is skipped.
    /// </summary>
    private async Task<IReadOnlyList<PriceTier>> BuildPriceLadderAsync(
        int tariffId, int lotId, DateTimeOffset checkIn, string plate, int consumerId, TimeSpan maxLot, CancellationToken ct)
    {
        using var phase = ApcoaTelemetry.Phase("apcoa.price_ladder");
        var candidates = await CandidateDurationsAsync(lotId, maxLot, ct);

        var tiers = new List<PriceTier>();
        decimal? lastPln = null;
        foreach (var minutes in candidates)
        {
            TariffBreakdown quote;
            try
            {
                quote = await QuoteAsync(tariffId, lotId, TimeSpan.FromMinutes(minutes), checkIn, plate, promo: null, consumerId, ct);
            }
            catch (Refit.ApiException)
            {
                continue; // this duration isn't quotable — skip it rather than fail the whole ladder
            }

            var pln = quote.TotalPayablePln;
            // Collapse: only record a tier when the price changes (so 30&60→60=2 PLN boundary).
            if (lastPln is { } lp && lp == pln)
                continue;
            tiers.Add(new PriceTier(minutes, pln));
            lastPln = pln;
        }

        return tiers;
    }

    /// <summary>
    /// Candidate ladder durations (minutes) to quote. Prefers the lot's tariff grid block boundaries
    /// (FLAT duration, then cumulative INCREMENTAL blocks up to the day) so boundaries are exact; falls
    /// back to a fixed ladder if the tariff can't be read. All capped at <paramref name="maxLot"/>.
    /// </summary>
    private async Task<IReadOnlyList<int>> CandidateDurationsAsync(int lotId, TimeSpan maxLot, CancellationToken ct)
    {
        var fallback = new[] { 15, 30, 60, 90, 120, 180, 240 };
        var cap = maxLot == TimeSpan.MaxValue ? int.MaxValue : (int)maxLot.TotalMinutes;

        try
        {
            var tariff = await api.GetTariffAsync(lotId, ct);
            var template = tariff?.TariffTemplates.FirstOrDefault(t => t.IsActive == 1 && t.IsDefault == 1)
                           ?? tariff?.TariffTemplates.FirstOrDefault(t => t.IsActive == 1)
                           ?? tariff?.TariffTemplates.FirstOrDefault();
            var slot = template?.Stacks.FirstOrDefault()?.Slots.FirstOrDefault();
            if (slot is not null && slot.Grids.Count > 0)
            {
                // Derive block boundaries: the cumulative end minute of the FLAT block and of each
                // following INCREMENTAL block, capped at the slot/lot max. Quoting at each boundary
                // captures the price step at that tier.
                var ordered = slot.Grids.OrderBy(g => g.Sequence).ToList();
                var bounds = new SortedSet<int>();
                var cursor = 0;
                foreach (var g in ordered)
                {
                    if (g.Duration <= 0) continue;
                    cursor += g.Duration;
                    bounds.Add(cursor);
                }
                var slotMax = slot.MaxMinutes is { } mm && mm > 0 ? mm : cap;
                var result = bounds.Where(m => m <= Math.Min(cap, slotMax)).OrderBy(m => m).ToList();
                if (result.Count > 0)
                    return result;
            }
        }
        catch (Refit.ApiException)
        {
            // fall through to the fixed ladder
        }

        return fallback.Where(m => m <= cap).ToArray();
    }

    /// <summary>Renders the non-shortest tiers as a trailing " Other options: …" clause (empty if ≤1 tier).</summary>
    private static string FormatLadderSuffix(IReadOnlyList<PriceTier> ladder)
    {
        if (ladder.Count <= 1) return string.Empty;
        var rest = ladder.Skip(1).Select(t => $"{t.Minutes} min = {t.Pln:0.00} PLN");
        return " Other options: " + string.Join("; ", rest) + ".";
    }

    /// <summary>Message for a free-only request at a parking that has no usable free tier: states it is
    /// not free, shows pricing, and points at the paid tool.</summary>
    private static string FormatNoFreeMessage(int parkingId, IReadOnlyList<PriceTier> ladder)
    {
        if (ladder.Count == 0)
            return $"No free parking is available at parking {parkingId}, and no price could be quoted. Use start_session to start a paid session.";
        var shortest = ladder[0];
        return $"No free parking is available at parking {parkingId}. Shortest paid option: {shortest.Minutes} min = {shortest.Pln:0.00} PLN." +
               FormatLadderSuffix(ladder) + " Use start_session to start a paid session.";
    }

    /// <summary>
    /// Builds a short note on whether an early stop is possible and whether it refunds, from the lot's
    /// config (allowRefund / enableStopButton) and the tariff's proration flag. Best-effort: returns
    /// null if nothing can be determined.
    /// </summary>
    private async Task<string?> BuildCancelRefundNoteAsync(int lotId, ParkingLot lot, CancellationToken ct)
    {
        using var phase = ApcoaTelemetry.Phase("apcoa.cancel_note");
        var cfg = lot.PrimaryConfig;
        var refunds = cfg?.AllowRefund == 1;

        bool? prorated = null;
        try
        {
            var tariff = await api.GetTariffAsync(lotId, ct);
            var template = tariff?.TariffTemplates.FirstOrDefault(t => t.IsActive == 1) ?? tariff?.TariffTemplates.FirstOrDefault();
            if (template is not null) prorated = template.IsProrationAllowed == 1;
        }
        catch (Refit.ApiException)
        {
            // leave proration unknown
        }

        // Only assert "cannot stop" when the config explicitly says so (0). If config is missing, don't
        // over-claim; fall through to the refund wording.
        if (cfg is not null && cfg.EnableStopButton == 0 && cfg.AllowRefund == 0)
            return "Early stop: this parking may not allow stopping early; no refund of remaining time.";

        if (refunds)
            return prorated == true
                ? "Early stop: allowed, with a prorated (partial) refund for unused time."
                : "Early stop: allowed, with a refund.";

        return "Early stop: allowed, but NO refund — the remaining paid time is forfeited.";
    }

    private Task<TariffBreakdown> QuoteAsync(
        int tariffId, int lotId, TimeSpan window, DateTimeOffset checkIn,
        string plate, Offer? promo, int consumerId, CancellationToken ct) =>
        api.GetTariffBreakdownAsync(TariffBreakdownQuery.For(
            lotId, tariffId, checkIn, window, plate,
            company: Company,
            promotionId: promo?.PromotionId,
            discountName: promo?.PromotionName,
            offerDiscountType: promo is null ? null : "PROMOTION",
            consumerUserId: consumerId), ct);

    /// <summary>
    /// Authoritatively checks whether <paramref name="promo"/> can actually be used right now (the
    /// tariff breakdown applies promos optimistically even when they are unusable, which leads to a
    /// free quote but a charged start). Returns true only when the promotion/validate endpoint returns
    /// a success status AND its typed body carries no error marker. Any non-success status, any body
    /// with an error code/message, or any transport error is treated as "not usable" (returns false).
    ///
    /// NOTE: the official APCOA app does NOT call promotion/validate — it trusts the tariff breakdown's
    /// 0-cost result directly. We keep this as an extra safety gate, but it MUST be fed the nested
    /// promotion name (<see cref="Offer.PromotionName"/>, e.g. "FRE60MIN07"), not the offer display name
    /// ("Darmowe60minutparkowania."); the latter makes validate return 422 INVALID_PROMOTION_CODE and the
    /// usable promo gets wrongly discarded.
    /// </summary>
    private async Task<bool> PromotionValidatesAsync(
        Offer promo, int lotId, string plate, int consumerId, DateTimeOffset startTime, TimeSpan window, CancellationToken ct)
    {
        using var phase = ApcoaTelemetry.Phase("apcoa.promotion.validate");
        try
        {
            var response = await api.ValidatePromotionAsync(new ValidatePromotionRequest
            {
                ConsumerID = consumerId,
                VehicleNumber = plate,
                PromotionName = promo.PromotionName ?? string.Empty,
                Company = Company,
                ParkingLot = lotId,
                StartTime = startTime,
                DurationValue = window,
            }, ct);

            // Must be a success status AND the typed body must not carry an error marker: an error
            // statusCode (outside 2xx, e.g. 5007) or a non-neutral message (e.g. INVALID_PROMOTION_CODE).
            if (!response.IsSuccessStatusCode)
                return false;

            var body = response.Content;
            if (body is null)
                return true; // success status, no body — nothing says the promo failed

            // Treat as usable on a 2xx UNLESS the body carries an explicit failure marker. The success
            // body says message:"valid", error:null (NOT "SUCCESS"/"OK"); a failure says
            // message:"INVALID_PROMOTION_CODE", statusCode:5007 (and usually arrives as a non-2xx caught
            // above). So reject only on a populated error, an out-of-2xx statusCode, or a message that
            // reads like an error — never on an unrecognised-but-neutral message.
            if (!string.IsNullOrWhiteSpace(body.Error))
                return false;

            if (body.StatusCode is { } code && (code < 200 || code > 299))
                return false;

            if (!string.IsNullOrWhiteSpace(body.Message) &&
                (body.Message.Contains("INVALID", StringComparison.OrdinalIgnoreCase) ||
                 body.Message.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                 body.Message.Contains("EXPIRED", StringComparison.OrdinalIgnoreCase) ||
                 body.Message.Contains("FAIL", StringComparison.OrdinalIgnoreCase)))
                return false;

            return true;
        }
        catch (Refit.ApiException ex)
        {
            // Transport/deserialisation failure — treat the promo as not usable rather than risk a charge.
            logger.LogWarning(ex, "PromotionValidates call failed for promo '{Promo}' (lot {Lot}): HTTP {Status} — treating as not usable.",
                promo.PromotionName, lotId, (int)ex.StatusCode);
            return false;
        }
    }

    /// <summary>
    /// A start that did not happen. Records <paramref name="outcome"/> on the current span and, unless
    /// <paramref name="isError"/> is false (an expected answer such as "not free"), marks it failed.
    /// </summary>
    private static StartResult Refused(string message, string outcome = "refused", bool isError = true)
    {
        if (isError)
            ApcoaTelemetry.Fail(outcome, message);
        else
            ApcoaTelemetry.Outcome(outcome);
        return new(false, 0m, 0, null, null, message, null) { Outcome = outcome };
    }

    private static string Normalize(string? s) => (s ?? string.Empty).Replace(" ", "").Trim();

    /// <summary>
    /// One-line description of an active session for the "already active" message: id, parking, plate,
    /// and how long it has left (from its scheduled checkout), or "overdue"/"end time unknown".
    /// </summary>
    private static string DescribeActive(ParkingSessionV2 s)
    {
        string remaining;
        if (s.ScheduledCheckoutTime is { } end)
        {
            var left = end - DateTimeOffset.UtcNow;
            remaining = left > TimeSpan.Zero
                ? $"{(int)left.TotalMinutes} min left"
                : "overdue (past its scheduled end)";
        }
        else
        {
            remaining = "end time unknown";
        }

        var plate = string.IsNullOrWhiteSpace(s.LicensePlate) ? "?" : s.LicensePlate;
        return $"session {s.Id} at '{s.ParkingName}' [{s.ParkingId}], plate {plate}, {remaining}";
    }
}
