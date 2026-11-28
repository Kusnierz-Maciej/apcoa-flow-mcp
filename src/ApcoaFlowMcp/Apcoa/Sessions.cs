using System.Globalization;
using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Models;
using Refit;

namespace ApcoaFlowMcp.Apcoa;

/// <summary>How a start treats cost.</summary>
public enum StartMode
{
    /// <summary>Free starts at once; paid needs the exact confirmed cost and stays under the cap.</summary>
    Normal,

    /// <summary>Only if the dated quote is 0.00; never charges.</summary>
    FreeOnly,

    /// <summary>Like <see cref="Normal"/> but without the cost cap.</summary>
    BypassCap,
}

/// <summary>What a start, extend or end did, as a tool result plus the outcome for logs and spans.</summary>
/// <param name="Error">A soft failure (refused, not free…): the span is marked Error.</param>
public sealed record SessionResult(string Text, string Outcome, bool Error = false, string? SessionId = null,
    decimal? CostPln = null, int? DurationMinutes = null);

/// <summary>
/// Starting, extending and ending sessions. Payment: always the account's default method, and only a saved card
/// (charged without any interaction); otherwise refuse before starting. Free starts send the default method's id too,
/// as the app does.
/// </summary>
public sealed class Sessions(IApcoaFlowApi api, Pricing pricing, TimeProvider time, ILogger<Sessions> logger)
{
    /// <summary>When the user gives no duration and the stay isn't free: the parking's preselected duration, else this.</summary>
    public const int DefaultDurationMinutes = 60;

    /// <param name="until">Instead of <paramref name="durationMinutes"/>: when the stay should end, in the parking's local
    /// time (<see cref="EndTime"/>).</param>
    /// <remarks>
    /// A stay is always stretched to the longest one at the same price (free: the free window; paid: read off the
    /// parking's hourly prices, verified with a quote). The first (quote) call says so; the start reports it.
    /// </remarks>
    public async Task<SessionResult> StartAsync(string parkingId, string licensePlate, int? durationMinutes,
        decimal? confirmedCostPln, StartMode mode, decimal maxCostPln, CancellationToken ct, string? until = null)
    {
        var vehicle = Plates.Find(await Phase("apcoa.vehicles", () => api.GetVehiclesAsync(ct)), licensePlate);
        if (vehicle is null)
            return Refused("plate_not_registered", $"\"{licensePlate}\" isn't one of the account's vehicles. Call get_vehicles for the plates.");
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.LicensePlate, vehicle.LicensePlate);

        var running = (await Phase("apcoa.running_sessions", () => api.GetSessionsAsync(ct)))
            .FirstOrDefault(s => Plates.Normalize(s.Vehicle?.LicensePlate) == Plates.Normalize(vehicle.LicensePlate));
        if (running is not null)
            return Refused("session_already_active",
                $"{vehicle.LicensePlate} already has a running session: {Describe(running)}. End it with end_session (or " +
                "extend it with extend_session) before starting another one.", running.Id);

        // The details with the hourly price curve: name, rules, usual/longest stay and the prices of a stay starting now.
        var parking = await Phase("apcoa.parking", () => pricing.DetailsWithCurveAsync(parkingId.Trim(), ct));
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ParkingId, parking.Id);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ParkingName, parking.Name);
        if (parking.CtaButtonType is not null && parking.CtaButtonType != "startParking")
            return Refused("not_startable", parking.SessionsStartAutomatically
                ? $"Sessions can't be started at {parking.Name}: it recognises plates and starts and ends sessions by itself " +
                  "when the car enters and leaves (see get_parking_details for its rules and free time)."
                : $"Sessions can't be started at {parking.Name} (not in the APCOA Flow app either).");

        var card = await Phase("apcoa.payment_method", () => DefaultMethodAsync(parking.Id, ct));
        if (card.Refusal is { } noMethod)
            return noMethod;

        var zone = ParkingTime.Zone(parking.CountryCode);
        string At(DateTimeOffset utc) => Text.LocalTime(utc, time, zone) + ParkingTime.Suffix(zone);
        DateTimeOffset? end = null;
        if (until is not null)
        {
            if (!EndTime.TryParse(until, time, zone, out var parsed, out var untilError))
                return Refused("bad_duration", untilError);
            end = parsed;
            durationMinutes = EndTime.MinutesFrom(Minute(time.GetUtcNow()), parsed);
        }
        if (durationMinutes is < 1)
            return Refused("bad_duration", "The duration must be at least 1 minute.");
        var duration = durationMinutes;
        var untilText = end is { } u ? $" (until {At(u)})" : "";
        var repeat = end is { } u2 ? $"until=\"{Text.LocalTime(u2, time, zone)}\"" : $"durationMinutes={duration}";
        Pricing.FreeWindow? window = null;
        if (duration is null || mode == StartMode.FreeOnly)
            window = await Phase("apcoa.free_window", () => pricing.FreeWindowAsync(parking, vehicle.Id, ct));
        // The whole known free window; when only "free now" is known (no length), the usual duration — the quote decides.
        duration ??= window is { Minutes: > 1 } free ? free.Minutes : parking.DefaultLengthOfStayInMinutes ?? DefaultDurationMinutes;
        if (parking.MaxDuration is > 0 && duration > parking.MaxDuration)
            return Refused("exceeds_max_duration", $"{parking.Name} allows stays of up to {Text.Duration(parking.MaxDuration.Value)}.");
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.DurationMinutes, duration);

        if (mode == StartMode.FreeOnly && window is { Minutes: 0 })
        {
            var price = (await Phase("apcoa.quote", () => pricing.QuoteAsync(parking.Id, vehicle.Id, duration.Value, ct))).Total;
            return NotFree($"{parking.Name} isn't free for {vehicle.LicensePlate} right now: {Text.Duration(duration.Value)} costs {price}. " +
                           "Nothing was started; use start_session to park there for a fee.", price.Amount);
        }
        if (mode == StartMode.FreeOnly && duration > window!.Value.Minutes && !window.Value.AtLeast)
            return NotFree($"{parking.Name} is free for {vehicle.LicensePlate} only up to {Text.Duration(window.Value.Minutes)} right now, " +
                           $"so {Text.Duration(duration.Value)} would cost money. Nothing was started; start with a shorter duration " +
                           "(or leave it out to use the whole free time).", null);

        var quote = await Phase("apcoa.quote", () => pricing.QuoteAsync(parking.Id, vehicle.Id, duration.Value, ct));
        var cost = quote.Total;
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.CostPln, (double)cost.Amount);
        if (!cost.IsZero)
        {
            if (mode != StartMode.FreeOnly && CardRefusal(card.Method!) is { } notACard)
                return notACard;
            if (mode == StartMode.FreeOnly)
                return NotFree($"{parking.Name} isn't free for {vehicle.LicensePlate} for {Text.Duration(duration.Value)}: it costs {cost}. Nothing was started.", cost.Amount);
            if (mode == StartMode.Normal && cost.Amount > maxCostPln)
                return Refused("cost_cap_exceeded",
                    $"{Text.Duration(duration.Value)} at {parking.Name} costs {cost}, more than this server's limit of " +
                    $"{maxCostPln.ToString("0.00", CultureInfo.InvariantCulture)} PLN. Nothing was started. Use start_expensive_session " +
                    "only if the user explicitly accepts this cost.");
            if (confirmedCostPln is null)
            {
                var longest = await StretchAsync();
                var amount = cost.Amount.ToString("0.00", CultureInfo.InvariantCulture);
                return new SessionResult(
                    $"{Text.Duration(duration.Value)}{untilText} at {parking.Name} for {vehicle.LicensePlate} costs {cost}, " +
                    $"{(quote.PrepaymentMode == "autoCharge" ? "charged at start" : "charged at the end")} to {card.Method!.Type} {card.Method.Identifier}." +
                    (window is { Minutes: > 0 } w ? $" (Free for up to {Text.Duration(w.Minutes)}.)" : "") +
                    (longest is { } l
                        ? $" The same {cost} covers up to {Text.Duration(l)}, so the session will run until about {At(Minute(time.GetUtcNow()).AddMinutes(l))} (same price, longer stay) — tell the user."
                        : "") +
                    $" Nothing was started yet: confirm with the user, then call again with {repeat} and confirmedCostPln={amount}.",
                    "quote", CostPln: cost.Amount, DurationMinutes: duration);
            }
            if (confirmedCostPln.Value != cost.Amount)
                return Refused("cost_mismatch",
                    $"The price changed: {Text.Duration(duration.Value)} at {parking.Name} now costs {cost} (you confirmed " +
                    $"{confirmedCostPln.Value.ToString("0.00", CultureInfo.InvariantCulture)} PLN). Nothing was started; confirm the new price with the user.");
        }

        // Same price, longer stay: always taken (the quote said so; free stays have no quote, the result says so).
        var requested = duration.Value;
        if (await StretchAsync() is { } stretched)
        {
            duration = stretched;
            ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.DurationMinutes, duration);
        }

        var start = Minute(time.GetUtcNow());
        var started = await Phase("apcoa.start", () => api.StartParkingAsync(new StartParkingRequest
        {
            SpaceId = parking.Id, LicensePlateId = vehicle.Id, PaymentMethodId = card.Method!.Id,
            StartDate = start, EndDate = start.AddMinutes(duration.Value),
        }, Guid.NewGuid().ToString(), ct));

        var sessionId = started.Id;
        string? failure = null;
        Session? runningNow;
        try
        {
            if (started.NeedsPaymentAction)
                failure = "payment_action_required";
            else if (started.IsPending)
            {
                var pending = await Phase("apcoa.pending", () => WaitForPendingAsync(started.PendingId!, ct));
                if (pending?.IsCompleted == true) sessionId = pending.SessionId;
                else if (pending?.IsFailed == true) failure = $"failed: {pending.FailureReason ?? pending.Status}";
                else failure = "still_pending";
            }

            // Whatever the answer said, the running list is the truth: never report "not started" while one runs.
            runningNow = (await Phase("apcoa.readback", () => api.GetSessionsAsync(ct))).FirstOrDefault(s =>
                (sessionId is not null && s.Id == sessionId) ||
                (Plates.Normalize(s.Vehicle?.LicensePlate) == Plates.Normalize(vehicle.LicensePlate) && s.Space?.Id == parking.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The start was sent and accepted: a failed read-back must not look like "try again" (a retry could charge twice).
            logger.LogWarning(ex, "start at {Parking} was accepted but couldn't be confirmed", parking.Id);
            return Refused("start_unconfirmed",
                $"APCOA accepted the start at {parking.Name}, but this server couldn't confirm it afterwards. Don't start " +
                "again: check get_active_sessions first.", sessionId);
        }

        if (failure == "payment_action_required")
        {
            logger.LogWarning("start at {Parking} needs an interactive payment step; ending anything that started.", parking.Id);
            if (runningNow is not null)
                await Phase("apcoa.stop", () => api.StopParkingAsync(runningNow.Id, ct));
            return Refused("payment_action_required",
                $"The card payment at {parking.Name} needs a confirmation step (e.g. 3-D Secure) that this server can't do. " +
                (runningNow is null ? "Nothing was started." : $"The session that began ({runningNow.Id}) was ended at once.") +
                " Try another parking, or start it in the APCOA Flow app.");
        }
        if (runningNow is null)
        {
            logger.LogWarning("start at {Parking} didn't produce a running session ({Failure}, pendingId {Pending}).", parking.Id, failure, started.PendingId);
            return Refused(failure is null or "still_pending" ? "start_unconfirmed" : "start_failed",
                failure is null or "still_pending"
                    ? $"APCOA accepted the start at {parking.Name} but the session isn't running yet. Check get_active_sessions in a moment before trying again."
                    : $"APCOA didn't start the session at {parking.Name} ({failure}). Nothing is running.");
        }

        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.SessionId, runningNow.Id);
        var what = cost.IsZero ? "free" : $"{cost}, {(quote.PrepaymentMode == "autoCharge" ? "charged at start" : "charged at the end")}";
        var stretchNote = duration > requested
            ? $" It runs until {At(start.AddMinutes(duration.Value))}: {Text.Duration(duration.Value)} cost the same as the {Text.Duration(requested)} asked for."
            : "";
        return new SessionResult($"Started: {Describe(runningNow)} ({what}).{stretchNote}", "started",
            SessionId: runningNow.Id, CostPln: cost.Amount, DurationMinutes: duration);

        // The longest stay at the same price as the requested one, when longer (verified with a quote); null if none
        // or the check fails (it's a bonus: never a reason not to quote or start).
        async Task<int?> StretchAsync()
        {
            try
            {
                if (cost.IsZero)
                {
                    window ??= await Phase("apcoa.free_window", () => pricing.FreeWindowAsync(parking, vehicle.Id, ct));
                    return window.Value.Minutes > duration ? window.Value.Minutes : null;
                }
                return await Phase("apcoa.same_price", () => pricing.LongestAtSamePriceAsync(parking, vehicle.Id, duration.Value, cost, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation("same-price check at {Parking} failed ({Error}); keeping the requested length", parking.Id, ex.Message);
                return null;
            }
        }
    }

    /// <summary>Polls an asynchronous start for up to ~30 s; null if it never settled.</summary>
    private async Task<PendingStartStatus?> WaitForPendingAsync(string pendingId, CancellationToken ct)
    {
        PendingStartStatus? last = null;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(2), time, ct);
            last = await api.GetPendingStartAsync(pendingId, ct);
            if (last.IsCompleted || last.IsFailed)
                return last;
        }
        logger.LogWarning("pending start {Pending} didn't settle (last status {Status})", pendingId, last?.Status);
        return last;
    }

    /// <param name="until">Instead of <paramref name="minutes"/>: the new end of the session, in the parking's local time.</param>
    /// <remarks>
    /// The extension is stretched to the next full hour of the session when that costs the same (most tariffs bill per
    /// started hour from the session's start), checked with one quote; the quote says so and the result reports it.
    /// </remarks>
    public async Task<SessionResult> ExtendAsync(string sessionId, int minutes, decimal? confirmedCostPln, decimal maxCostPln, CancellationToken ct,
        string? until = null)
    {
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.SessionId, sessionId);
        if (until is null && minutes < 1)
            return Refused("bad_duration", "Extend by at least 1 minute.");
        var session = (await Phase("apcoa.running_sessions", () => api.GetSessionsAsync(ct))).FirstOrDefault(s => s.Id == sessionId.Trim());
        if (session is null)
            return Refused("session_not_found", "No running session has that id. Call get_active_sessions for the current ones.");
        var zone = ApcoaFlowEndpoints.AppTimeZone;
        if (session.Space?.Id is { } spaceId)
        {
            try { zone = ParkingTime.Zone((await Phase("apcoa.parking", () => api.GetSpaceAsync(spaceId, ct: ct))).CountryCode); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation("no details for {Parking} ({Error}); times in the app's zone", spaceId, ex.Message);
            }
        }
        string At(DateTimeOffset utc) => Text.LocalTime(utc, time, zone) + ParkingTime.Suffix(zone);
        if (until is not null)
        {
            if (!EndTime.TryParse(until, time, zone, out var newEndWanted, out var untilError))
                return Refused("bad_duration", untilError);
            if (session.EndDate is not { } currentEnd)
                return Refused("bad_duration", $"{Describe(session)} has no end time to extend from; give minutes instead.");
            minutes = EndTime.MinutesFrom(currentEnd, newEndWanted);
            if (minutes < 1)
                return Refused("bad_duration", $"{Describe(session)} already runs until {At(currentEnd)}; nothing to extend.");
        }
        if (!session.SessionCanBeExtended)
            return Refused("not_extendable", $"{Describe(session)} can't be extended.");

        var card = await Phase("apcoa.payment_method", () => DefaultMethodAsync(session.Space?.Id, ct));
        if (card.Refusal is { } noMethod)
            return noMethod;

        var quote = await Phase("apcoa.quote", () => api.GetUpdatedPriceAsync(session.Id, minutes * 60, ct));
        var cost = quote.Total;
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.CostPln, (double)cost.Amount);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.DurationMinutes, minutes);
        var requestedMinutes = minutes;
        var requestedEnd = quote.EndDate ?? (session.EndDate ?? time.GetUtcNow()).AddMinutes(minutes);
        var newEnd = requestedEnd;

        // Same price, longer: the next full hour of the session, when one quote says it costs the same.
        (int Minutes, DateTimeOffset End)? stretch = null;
        try
        {
            if (session.EndDate is { } oldEnd)
            {
                var hours = Math.Ceiling((requestedEnd - session.StartDate).TotalMinutes / 60 - 1e-9);
                var candidate = EndTime.MinutesFrom(oldEnd, session.StartDate.AddHours(hours));
                if (candidate > minutes)
                {
                    var longer = await Phase("apcoa.same_price", () => api.GetUpdatedPriceAsync(session.Id, candidate * 60, ct));
                    if (longer.Total.Amount == cost.Amount)
                        stretch = (candidate, longer.EndDate ?? oldEnd.AddMinutes(candidate));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation("same-price check for extending {Session} failed ({Error})", session.Id, ex.Message);
        }
        if (!cost.IsZero)
        {
            if (CardRefusal(card.Method!) is { } notACard)
                return notACard;
            // The cap is for the whole session: what it already costs plus this extension.
            var sessionCost = SessionTotal(session);
            if (sessionCost + cost.Amount > maxCostPln)
                return Refused("cost_cap_exceeded",
                    $"Extending by {Text.Duration(minutes)} costs {cost}; with the {sessionCost.ToString("0.00", CultureInfo.InvariantCulture)} PLN " +
                    $"the session already costs that's more than this server's limit of " +
                    $"{maxCostPln.ToString("0.00", CultureInfo.InvariantCulture)} PLN per session. Nothing was changed.");
            if (confirmedCostPln is null)
                return new SessionResult(
                    $"Extending {Describe(session)} by {Text.Duration(minutes)} (until {At(newEnd)}) costs {cost}." +
                    (stretch is { } st ? $" The same {cost} covers until {At(st.End)}, so it will be extended until then (same price, longer stay) — tell the user." : "") +
                    $" Nothing was changed yet: confirm with the user, then call again with the same arguments and confirmedCostPln=" +
                    $"{cost.Amount.ToString("0.00", CultureInfo.InvariantCulture)}.", "quote", SessionId: session.Id, CostPln: cost.Amount);
            if (confirmedCostPln.Value != cost.Amount)
                return Refused("cost_mismatch",
                    $"The price changed: extending by {Text.Duration(minutes)} now costs {cost} (you confirmed " +
                    $"{confirmedCostPln.Value.ToString("0.00", CultureInfo.InvariantCulture)} PLN). Nothing was changed.");
        }

        if (stretch is { } taken)
        {
            (minutes, newEnd) = taken;
            ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.DurationMinutes, minutes);
        }

        var answer = await Phase("apcoa.extend", () => api.UpdateParkingDurationAsync(new UpdateParkingDurationRequest
        {
            SessionId = session.Id, NewEnd = newEnd, PaymentMethodId = card.Method!.Id,
        }, ct));
        if (!answer.IsSuccessStatusCode)
            throw (Exception?)answer.Error ?? new HttpRequestException($"updateParkingDuration: HTTP {(int?)answer.StatusCode}");
        var body = answer.Content; // null for an empty body (a free extension, 2026-11-27)
        if (body is not null)
            logger.LogInformation("extend {Session} answered status {Status}, pending {Pending}, failure {Failure}",
                session.Id, body.Status, body.PendingId, body.FailureReason);
        var paid = cost.IsZero ? "free" : cost.ToString();
        if (!string.IsNullOrEmpty(body?.ClientSecret))
            return Refused("payment_action_required",
                $"The card payment for extending the session at {session.Space?.Name} needs a confirmation step (e.g. 3-D " +
                "Secure) that this server can't do. Check get_active_sessions: the session keeps its old end unless it shows otherwise.");
        if (!string.IsNullOrEmpty(body?.FailureReason) || IsFailedStatus(body?.Status))
            return Refused("extend_failed", $"APCOA didn't extend the session at {session.Space?.Name} ({body!.FailureReason ?? body.Status}).");
        if (!string.IsNullOrEmpty(body?.PendingId))
        {
            // A paid extension answers {status: awaitingPayment, pendingId}; sessions/pending/{id} settles it (2026-11-27).
            PendingStartStatus? pending = null;
            try { pending = await Phase("apcoa.pending", () => WaitForPendingAsync(body.PendingId!, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "pending extension {Pending} not readable", body.PendingId);
            }
            if (pending?.IsFailed == true)
                return Refused("extend_failed",
                    $"APCOA didn't extend the session at {session.Space?.Name} ({pending.FailureReason ?? pending.Status}). It keeps its old end.");
        }

        // Accepted (possibly processed asynchronously, like paid starts): report it only once the new end shows.
        Session? updated = null;
        try
        {
            updated = await Phase("apcoa.readback", () => WaitForEndAsync(session.Id, session.EndDate, newEnd, minutes, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "extended session {Session} but couldn't re-read it", session.Id);
        }
        if (updated is null)
            return Refused("extend_unconfirmed",
                $"APCOA accepted extending the session at {session.Space?.Name} until {At(newEnd)} ({paid}), but " +
                "the new end isn't showing yet. Don't extend again: check get_active_sessions in a moment.", session.Id);
        var stretchNote = minutes > requestedMinutes
            ? $" Extended until {At(newEnd)} instead of {At(requestedEnd)}: it cost the same."
            : "";
        return new SessionResult($"Extended: {Describe(updated)} ({paid}).{stretchNote}", "extended",
            SessionId: session.Id, CostPln: cost.Amount, DurationMinutes: minutes);
    }

    /// <summary>
    /// The running session once its end has moved past <paramref name="oldEnd"/> and reached <paramref name="newEnd"/>
    /// (with a few seconds' slack, never more than half the extension); null after ~20 s. Each read is retried on its own.
    /// </summary>
    private async Task<Session?> WaitForEndAsync(string sessionId, DateTimeOffset? oldEnd, DateTimeOffset newEnd, int minutes, CancellationToken ct)
    {
        var slack = TimeSpan.FromSeconds(Math.Min(30, minutes * 30));
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(2), time, ct);
            try
            {
                var current = (await api.GetSessionsAsync(ct)).FirstOrDefault(s => s.Id == sessionId);
                if (current?.EndDate is { } end && (oldEnd is null || end > oldEnd.Value) && end >= newEnd - slack)
                    return current;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation("re-reading session {Session} failed ({Error}); retrying", sessionId, ex.Message);
            }
        }
        return null;
    }

    private static bool IsFailedStatus(string? status) => status is { } s &&
        (s.Contains("fail", StringComparison.OrdinalIgnoreCase) || s.Contains("reject", StringComparison.OrdinalIgnoreCase) ||
         s.Contains("cancel", StringComparison.OrdinalIgnoreCase) || s.Contains("declin", StringComparison.OrdinalIgnoreCase));

    public async Task<SessionResult> EndAsync(string sessionId, CancellationToken ct)
    {
        var id = sessionId.Trim();
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.SessionId, id);
        var session = (await Phase("apcoa.running_sessions", () => api.GetSessionsAsync(ct))).FirstOrDefault(s => s.Id == id);
        if (session is null)
            return Refused("session_not_found", "No running session has that id (it may have ended already). Call get_active_sessions for the current ones.");
        if (!session.SessionCanBeStopped)
            return Refused("not_stoppable", $"{Describe(session)} can't be stopped early; it ends by itself at its end time.");

        await Phase("apcoa.stop", () => api.StopParkingAsync(id, ct));

        // The bill can take a moment (post-pay sessions are charged now): wait until history has it settled.
        Money? cost = null;
        for (var attempt = 0; attempt < 6 && cost is null; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(2), time, ct);
            try
            {
                var details = await Phase("apcoa.bill", () => api.GetSessionsHistoryDetailsAsync(id, ct));
                if (!details.IsSessionActive && Money.TryParse(details.TotalPrice, out var billed))
                    cost = billed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The stop happened; a failed (or 404, post-pay settling) history read only means the cost is unknown yet.
                logger.LogDebug(ex, "history of stopped session {Session} not readable yet", id);
            }
        }
        var name = session.Space?.Name ?? "the parking";
        if (cost is not { } final)
            return new SessionResult($"Ended the session at {name} ({session.Vehicle?.LicensePlate}). The final cost isn't available yet; check get_session_history in a minute.",
                "stopped", SessionId: id);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.CostPln, (double)final.Amount);
        return new SessionResult($"Ended the session at {name} ({session.Vehicle?.LicensePlate}). Final cost: {final}.", "stopped",
            SessionId: id, CostPln: final.Amount);
    }

    /// <summary>
    /// The account's default payment method (the app sends its id even for free starts). Whether this server may
    /// charge it is <see cref="CardRefusal"/>, checked only when something costs money.
    /// </summary>
    private async Task<(PaymentMethod? Method, SessionResult? Refusal)> DefaultMethodAsync(string? parkingId, CancellationToken ct)
    {
        var methods = await api.GetPaymentMethodsAsync(parkingId, ct);
        var def = methods.FirstOrDefault(m => m.IsDefault);
        return def is null
            ? (null, Refused("no_payment_method",
                "The account has no default payment method this parking accepts. Set a saved card as the default in the APCOA Flow app."))
            : (def, null);
    }

    /// <summary>Null when the default method is a saved card this server may charge; else the refusal.</summary>
    private static SessionResult? CardRefusal(PaymentMethod def) => IsSavedCard(def)
        ? null
        : Refused("default_not_a_card",
            $"The account's default payment method is {def.Type} {def.Identifier}, which this server can't charge without you " +
            "(it needs a saved card that's charged without any extra step). Set a saved card as the default in the APCOA Flow app.");

    /// <summary>What a running session costs so far: its "Total" detail line (<c>{title: "Total", subTitle: "1", footer: "PLN"}</c>).</summary>
    internal static decimal SessionTotal(Session s) =>
        s.Details.FirstOrDefault(d => string.Equals(d.Title, "Total", StringComparison.OrdinalIgnoreCase)) is { SubTitle: { } amount } &&
        decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var total) ? total : 0m;

    /// <summary>A card charged without interaction: <c>flow: saved</c>, no action, available, not a wallet.</summary>
    public static bool IsSavedCard(PaymentMethod m) =>
        m.Flow == "saved" && m.RequiredAction == "none" && m.Availability == "available" &&
        m.Type is not ("applePay" or "googlePay" or "unknown");

    private string Describe(Session s)
    {
        var end = s.EndDate is { } e ? $" until {Text.LocalTime(e, time)}" : "";
        return $"[{s.Id}] {s.Space?.Name ?? "unknown parking"} - {s.Vehicle?.LicensePlate}, from {Text.LocalTime(s.StartDate, time)}{end}";
    }

    /// <summary>
    /// One API step as a child span of the tool span (so a trace shows which call took the time). The span closes before
    /// the result is used, so tool-level tags set afterwards land on the tool span, not on the phase.
    /// </summary>
    private static async Task<T> Phase<T>(string name, Func<Task<T>> step)
    {
        using var _ = ApcoaTelemetry.Phase(name);
        return await step();
    }

    private static async Task Phase(string name, Func<Task> step)
    {
        using var _ = ApcoaTelemetry.Phase(name);
        await step();
    }

    private static DateTimeOffset Minute(DateTimeOffset t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, TimeSpan.Zero);

    private static SessionResult Refused(string outcome, string text, string? sessionId = null) => new(text, outcome, Error: true, SessionId: sessionId);

    private static SessionResult NotFree(string text, decimal? cost) => new(text, "not_free", CostPln: cost);
}
