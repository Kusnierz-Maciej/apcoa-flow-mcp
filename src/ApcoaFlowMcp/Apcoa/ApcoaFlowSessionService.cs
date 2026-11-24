using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Models.Consumer;
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
public sealed class ApcoaFlowSessionService(IApcoaFlowApi api, IApcoaParkingDataSource parkingData)
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
        /// <summary>The parking the session targets (set once resolved).</summary>
        public int ParkingId { get; init; }
        public string? ParkingName { get; init; }

        /// <summary>Distance from the user to the parking, when started via current location.</summary>
        public int? DistanceMeters { get; init; }

        /// <summary>When non-null, there is already an active session at this parking.</summary>
        public ParkingSessionV2? ExistingSession { get; init; }
    }

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
        var nearby = await parkingData.FindNearbyAsync(latitude, longitude, radiusMeters / 1000.0, ct: ct);
        var closest = nearby.FirstOrDefault();
        if (closest.Parking is null)
            return Refused($"No parking found within {radiusMeters:0} m of your location.");

        var distanceMeters = (int)Math.Round(closest.DistanceKm * 1000);
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
        // 1. Resolve lot + tariff from the public parkingLots endpoint.
        var lots = await api.GetParkingLotsAsync(parkingId, ct: ct);
        var lot = lots.FirstOrDefault(l => l.LotId is not null && l.PrimaryTariffId is not null);
        if (lot?.LotId is null || lot.PrimaryTariffId is null)
            return Refused($"Could not resolve a bookable lot/tariff for parking {parkingId}.");

        var lotId = lot.LotId.Value;
        var tariffId = lot.PrimaryTariffId.Value;
        var parkingName = lot.NearbyParkingLot?.Name;
        var maxLot = lot.MaxDuration is { } m && m > TimeSpan.Zero ? m : TimeSpan.MaxValue;

        // Stamps parking identity onto any result produced once the parking is resolved.
        StartResult Tag(StartResult r) => r with { ParkingId = parkingId, ParkingName = parkingName };

        // 2. Resolve the consumer: vehicle (by plate or default), default payment method, profile id.
        var consumer = await api.GetConsumerAsync(ct: ct);
        var vehicle = consumer.VehicleInformationResponses
            .FirstOrDefault(v => string.Equals(Normalize(v.VehicleNumber), Normalize(licensePlate), StringComparison.OrdinalIgnoreCase));
        if (vehicle is null)
            return Refused($"License plate '{licensePlate}' is not registered on the account. Use get_registered_cars to see available plates.");

        var payment = consumer.ConsumerUserPaymentMethods.FirstOrDefault(p => p.IsDefault == 1)
                      ?? consumer.ConsumerUserPaymentMethods.FirstOrDefault();
        if (payment is null)
            return Refused("No payment method is configured on the account.");

        // The session's consumerProfileId is the default profile's id, NOT the top-level consumer id.
        if (consumer.DefaultProfileId is not { } profileId)
            return Refused("No consumer profile is available on the account.");

        // 3. Find an active free-time promotion for this lot (needed for OnlyFree; opportunistic otherwise).
        var offers = await api.GetApplicableOffersV2Async(new ApplicableOffersV2Request
        {
            ParkingLot = lotId,
            UserId = consumer.Id,
            Company = Company,
        }, ct);
        var promo = offers.Entities.FirstOrDefault(o => o.IsActiveFreeTime);

        // 4. Decide the duration.
        //    When a free-time promo applies, always fill the whole free window (a free coupon is
        //    one-use, so never waste part of it): a request shorter than the free window is rounded
        //    UP to it, and OnlyFree rejects a request longer than it. Everything stays capped by the
        //    lot's max duration.
        var requested = durationMinutes is { } dm ? TimeSpan.FromMinutes(dm) : (TimeSpan?)null;
        TimeSpan window;
        if (mode == StartMode.OnlyFree)
        {
            if (promo?.FreeWindow is not { } freeWindow)
                return Tag(Refused($"No active free-parking promotion is available for parking {parkingId}."));

            if (requested is { } r && r > freeWindow)
                return Tag(Refused($"Requested {r.TotalMinutes:0} min exceeds the free window of {freeWindow.TotalMinutes:0} min for this parking."));

            // Fill the whole free window (round a shorter request up to it).
            window = requested is { } rq && rq > freeWindow ? rq : freeWindow;
            window = window <= maxLot ? window : maxLot;
        }
        else
        {
            window = requested ?? TimeSpan.FromMinutes(DefaultDurationMinutes);

            // If a free promo applies, never park for less than the full free window — fill the slot,
            // then any paid overage runs after it.
            if (promo?.FreeWindow is { } freeWindow && window < freeWindow)
                window = freeWindow;

            window = window <= maxLot ? window : maxLot;
        }

        var checkIn = DateTimeOffset.UtcNow;
        var checkOut = checkIn.Add(window);

        // 5. If a promo applies, VALIDATE it before trusting it. The tariff breakdown (step 6) will
        //    optimistically zero out the price for a promo even when that promo is actually unusable
        //    (expired/used-up/not-valid-for-this-account), and the real start then charges full price.
        //    The promotion/validate endpoint is the authoritative check: a usable coupon returns a 2xx
        //    whose body carries no error signal; an unusable one returns a non-2xx (e.g. 422
        //    INVALID_PROMOTION_CODE / 5007) OR a 2xx body that still carries an error code/message.
        //    If validation does not clearly succeed, DROP the promo and fall back to the base price so
        //    the quote reflects what will really be charged.
        if (promo is not null && !await PromotionValidatesAsync(promo, lotId, vehicle.VehicleNumber, consumer.Id, checkIn, window, ct))
            promo = null;

        // 6. Quote the final (promo-applied) price.
        var quote = await QuoteAsync(tariffId, lotId, window, checkIn, vehicle.VehicleNumber, promo, consumer.Id, ct);
        var costPln = quote.TotalPayablePln;
        var minutes = (int)window.TotalMinutes;

        // A note describing the free window applied by a promo, so the paid confirmation makes clear
        // how much of the time is free vs charged (empty when no free-time promo applies).
        var freeNote = promo?.FreeMinutes is { } fm
            ? $" A {fm}-min free promotion ('{promo.Name}') is applied, so you are only charged for the time beyond it."
            : string.Empty;

        // OnlyFree must actually be free.
        if (mode == StartMode.OnlyFree && !quote.IsFree)
            return Tag(Refused($"This parking is not free for {minutes} min (cost {costPln:0.00} PLN) even with promotions."));

        // 7. Free → start now. Paid → require confirmation and the cap.
        if (!quote.IsFree)
        {
            if (!bypassCap && costPln > maxCostPln)
                return Tag(new StartResult(false, costPln, minutes, promo?.Name, promo?.FreeMinutes,
                    $"Cost {costPln:0.00} PLN exceeds the limit of {maxCostPln:0.00} PLN. Use start_expensive_session to override.{freeNote}",
                    null));

            if (confirmedCostPln is null)
                return Tag(new StartResult(false, costPln, minutes, promo?.Name, promo?.FreeMinutes,
                    $"This is a PAID session: {costPln:0.00} PLN for {minutes} min. Re-invoke with confirmedCostPln={costPln:0.00} to start.{freeNote}",
                    null));

            // The confirmed cost must match the live cost exactly — refuse if it differs either way
            // (price changed, promo expired, or a stale/guessed confirmation).
            if (costPln != confirmedCostPln.Value)
                return Tag(new StartResult(false, costPln, minutes, promo?.Name, promo?.FreeMinutes,
                    $"Live cost {costPln:0.00} PLN does not match the confirmed {confirmedCostPln.Value:0.00} PLN; not starting. Re-confirm with the exact current cost.{freeNote}",
                    null));
        }

        // 8. Start the session.
        StartSessionResponse response;
        try
        {
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
                PromotionName = promo?.Name,
                PromotionId = promo?.Id,
                OfferDiscountType = promo is null ? null : "PROMOTION",
                IsAutoApplyPromotion = promo is not null,
            }, ct);
        }
        catch (Refit.ApiException ex) when ((int)ex.StatusCode == 424)
        {
            // The API rejects a start with HTTP 424 FailedDependency / LIMIT_EXCEEDED (statusCode 5012)
            // when a session is already active (one at a time per account). Fetch the active session(s)
            // and tell the user whether one is already running HERE (so they may just use it) or
            // ELSEWHERE (so they can end it and retry), including how long each has left.
            var stillActive = await api.GetParkingSessionsAsync(
                new ParkingSessionV2Query { Status = "ACTIVE", PageSize = 10 }, ct);

            if (stillActive.Count == 0)
                return Tag(Refused("The API rejected the start with LIMIT_EXCEEDED, but no active session was found. Try again in a moment, or call get_active_sessions."));

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

            return Tag(new StartResult(false, 0m, 0, null, null, sb.ToString(), null) { ExistingSession = primary });
        }

        var kind = quote.IsFree ? "free" : $"{costPln:0.00} PLN";
        return Tag(new StartResult(true, costPln, minutes, promo?.Name, promo?.FreeMinutes,
            $"Started a {kind} session for {minutes} min on parking {parkingId}.", response));
    }

    /// <summary>Stops a session and returns the final (completed) session with its cost.</summary>
    public async Task<ParkingSessionV2?> StopAsync(long sessionId, CancellationToken ct)
    {
        await api.StopParkingSessionAsync(new StopParkingSessionRequest
        {
            ParkingSessionId = sessionId,
            SessionCheckOutTime = DateTimeOffset.UtcNow,
        }, ct);

        var completed = await api.GetParkingSessionsAsync(new ParkingSessionV2Query { Status = "COMPLETED", PageSize = 10 }, ct);
        return completed.FirstOrDefault(s => s.Id == sessionId);
    }

    public Task<List<ParkingSessionV2>> GetActiveAsync(CancellationToken ct) =>
        api.GetParkingSessionsAsync(new ParkingSessionV2Query { Status = "ACTIVE", PageSize = 10 }, ct);

    public Task<List<ParkingSessionV2>> GetHistoryAsync(CancellationToken ct) =>
        api.GetParkingSessionsAsync(new ParkingSessionV2Query { Status = "COMPLETED", PageSize = 10 }, ct);

    public async Task<List<Vehicle>> GetVehiclesAsync(CancellationToken ct) =>
        (await api.GetConsumerAsync(ct: ct)).VehicleInformationResponses;

    // -------------------------------------------------------------------------

    private Task<TariffBreakdown> QuoteAsync(
        int tariffId, int lotId, TimeSpan window, DateTimeOffset checkIn,
        string plate, Offer? promo, int consumerId, CancellationToken ct) =>
        api.GetTariffBreakdownAsync(TariffBreakdownQuery.For(
            lotId, tariffId, checkIn, window, plate,
            company: Company,
            promotionId: promo?.Id,
            discountName: promo?.Name,
            offerDiscountType: promo is null ? null : "PROMOTION",
            consumerUserId: consumerId), ct);

    /// <summary>
    /// Authoritatively checks whether <paramref name="promo"/> can actually be used right now (the
    /// tariff breakdown applies promos optimistically even when they are unusable, which leads to a
    /// free quote but a charged start). Returns true only when the promotion/validate endpoint returns
    /// a success status AND its typed body carries no error marker. Any non-success status, any body
    /// with an error code/message, or any transport error is treated as "not usable" (returns false).
    /// </summary>
    private async Task<bool> PromotionValidatesAsync(
        Offer promo, int lotId, string plate, int consumerId, DateTimeOffset startTime, TimeSpan window, CancellationToken ct)
    {
        try
        {
            var response = await api.ValidatePromotionAsync(new ValidatePromotionRequest
            {
                ConsumerID = consumerId,
                VehicleNumber = plate,
                PromotionName = promo.Name ?? string.Empty,
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

            if (body.StatusCode is { } code && (code < 200 || code > 299))
                return false;

            if (!string.IsNullOrWhiteSpace(body.Message) &&
                !body.Message.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase) &&
                !body.Message.Equals("OK", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }
        catch (Refit.ApiException)
        {
            // Transport/deserialisation failure — treat the promo as not usable rather than risk a charge.
            return false;
        }
    }

    private static StartResult Refused(string message) =>
        new(false, 0m, 0, null, null, message, null);

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
