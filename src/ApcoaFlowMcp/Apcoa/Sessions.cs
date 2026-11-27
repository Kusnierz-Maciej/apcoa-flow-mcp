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

    public async Task<SessionResult> StartAsync(string spaceId, string licensePlate, int? durationMinutes,
        decimal? confirmedCostPln, StartMode mode, decimal maxCostPln, CancellationToken ct)
    {
        var vehicle = Plates.Find(await api.GetVehiclesAsync(ct), licensePlate);
        if (vehicle is null)
            return Refused("plate_not_registered", $"\"{licensePlate}\" isn't one of the account's vehicles. Call get_vehicles for the plates.");
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.LicensePlate, vehicle.LicensePlate);

        var running = (await api.GetSessionsAsync(ct)).FirstOrDefault(s => Plates.Normalize(s.Vehicle?.LicensePlate) == Plates.Normalize(vehicle.LicensePlate));
        if (running is not null)
            return Refused("session_already_active",
                $"{vehicle.LicensePlate} already has a running session: {Describe(running)}. End it with end_session (or " +
                "extend it with extend_session) before starting another one.", running.Id);

        var space = await api.GetSpaceAsync(spaceId.Trim(), ct);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ParkingId, space.Id);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ParkingName, space.Name);

        var card = await DefaultCardAsync(space.Id, ct);
        if (card.Refusal is { } noCard)
            return noCard;

        if (durationMinutes is < 1)
            return Refused("bad_duration", "The duration must be at least 1 minute.");
        var duration = durationMinutes;
        Pricing.FreeWindow? window = null;
        if (duration is null || mode == StartMode.FreeOnly)
            window = await pricing.FreeWindowAsync(space.Id, vehicle.Id, space.DefaultLengthOfStayInMinutes, space.MaxDuration, ct);
        duration ??= window is { Minutes: > 0 } free ? free.Minutes : space.DefaultLengthOfStayInMinutes ?? DefaultDurationMinutes;
        if (space.MaxDuration is > 0 && duration > space.MaxDuration)
            return Refused("exceeds_max_duration", $"{space.Name} allows stays of up to {Text.Duration(space.MaxDuration.Value)}.");
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.DurationMinutes, duration);

        if (mode == StartMode.FreeOnly && window is { Minutes: 0 })
        {
            var price = (await pricing.QuoteAsync(space.Id, vehicle.Id, duration.Value, ct)).Total;
            return NotFree($"{space.Name} isn't free for {vehicle.LicensePlate} right now: {Text.Duration(duration.Value)} costs {price}. " +
                           "Nothing was started; use start_session to park there for a fee.", price.Amount);
        }
        if (mode == StartMode.FreeOnly && duration > window!.Value.Minutes)
            return NotFree($"{space.Name} is free for {vehicle.LicensePlate} only up to {Text.Duration(window.Value.Minutes)} right now, " +
                           $"so {Text.Duration(duration.Value)} would cost money. Nothing was started; start with a shorter duration " +
                           "(or leave it out to use the whole free time).", null);

        var quote = await pricing.QuoteAsync(space.Id, vehicle.Id, duration.Value, ct);
        var cost = quote.Total;
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.CostPln, (double)cost.Amount);
        if (!cost.IsZero)
        {
            if (mode == StartMode.FreeOnly)
                return NotFree($"{space.Name} isn't free for {vehicle.LicensePlate} for {Text.Duration(duration.Value)}: it costs {cost}. Nothing was started.", cost.Amount);
            if (mode == StartMode.Normal && cost.Amount > maxCostPln)
                return Refused("cost_cap_exceeded",
                    $"{Text.Duration(duration.Value)} at {space.Name} costs {cost}, more than this server's limit of " +
                    $"{maxCostPln.ToString("0.00", CultureInfo.InvariantCulture)} PLN. Nothing was started. Use start_expensive_session " +
                    "only if the user explicitly accepts this cost.");
            if (confirmedCostPln is null)
                return new SessionResult(
                    $"{Text.Duration(duration.Value)} at {space.Name} for {vehicle.LicensePlate} costs {cost}, " +
                    $"{(quote.PrepaymentMode == "autoCharge" ? "charged at start" : "charged at the end")} to {card.Method!.Type} {card.Method.Identifier}." +
                    (window is { Minutes: > 0 } w ? $" (Free for up to {Text.Duration(w.Minutes)}.)" : "") +
                    $" Nothing was started yet: confirm with the user, then call again with durationMinutes={duration} and " +
                    $"confirmedCostPln={cost.Amount.ToString("0.00", CultureInfo.InvariantCulture)}.",
                    "quote", CostPln: cost.Amount, DurationMinutes: duration);
            if (confirmedCostPln.Value != cost.Amount)
                return Refused("cost_mismatch",
                    $"The price changed: {Text.Duration(duration.Value)} at {space.Name} now costs {cost} (you confirmed " +
                    $"{confirmedCostPln.Value.ToString("0.00", CultureInfo.InvariantCulture)} PLN). Nothing was started; confirm the new price with the user.");
        }

        var start = Minute(time.GetUtcNow());
        var started = await api.StartParkingAsync(new StartParkingRequest
        {
            SpaceId = space.Id, LicensePlateId = vehicle.Id, PaymentMethodId = card.Method!.Id,
            StartDate = start, EndDate = start.AddMinutes(duration.Value),
        }, Guid.NewGuid().ToString(), ct);

        if (started.NeedsPaymentAction)
        {
            logger.LogWarning("start at {Space} needed a payment action (pendingId {Pending}, clientSecret {HasSecret}); ending it.",
                space.Id, started.PendingId, started.ClientSecret is not null);
            if (started.Id is { } toEnd)
            {
                try { await api.StopParkingAsync(toEnd, ct); }
                catch (ApiException ex) { logger.LogWarning(ex, "could not end session {Id} after a payment action was needed", toEnd); }
            }
            return Refused("payment_action_required",
                $"The card payment at {space.Name} needs a confirmation step (e.g. 3-D Secure) that this server can't do, " +
                "so the session was not started (anything begun was ended at once). Try another parking, or start it in the APCOA Flow app.");
        }

        var session = (await api.GetSessionsAsync(ct)).FirstOrDefault(s => s.Id == started.Id);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.SessionId, started.Id);
        var what = cost.IsZero ? "free" : $"{cost}, {(quote.PrepaymentMode == "autoCharge" ? "charged at start" : "charged at the end")}";
        return new SessionResult(
            $"Started: {(session is null ? $"[{started.Id}] {space.Name} - {vehicle.LicensePlate}" : Describe(session))} ({what}).",
            "started", SessionId: started.Id, CostPln: cost.Amount, DurationMinutes: duration);
    }

    public async Task<SessionResult> ExtendAsync(string sessionId, int minutes, decimal? confirmedCostPln, decimal maxCostPln, CancellationToken ct)
    {
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.SessionId, sessionId);
        if (minutes < 1)
            return Refused("bad_duration", "Extend by at least 1 minute.");
        var session = (await api.GetSessionsAsync(ct)).FirstOrDefault(s => s.Id == sessionId.Trim());
        if (session is null)
            return Refused("session_not_found", "No running session has that id. Call get_active_sessions for the current ones.");
        if (!session.SessionCanBeExtended)
            return Refused("not_extendable", $"{Describe(session)} can't be extended.");

        var card = await DefaultCardAsync(session.Space?.Id, ct);
        if (card.Refusal is { } noCard)
            return noCard;

        var quote = await api.GetUpdatedPriceAsync(session.Id, minutes * 60, ct);
        var cost = quote.Total;
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.CostPln, (double)cost.Amount);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.DurationMinutes, minutes);
        var newEnd = quote.EndDate ?? (session.EndDate ?? time.GetUtcNow()).AddMinutes(minutes);
        if (!cost.IsZero)
        {
            if (cost.Amount > maxCostPln)
                return Refused("cost_cap_exceeded",
                    $"Extending by {Text.Duration(minutes)} costs {cost}, more than this server's limit of " +
                    $"{maxCostPln.ToString("0.00", CultureInfo.InvariantCulture)} PLN. Nothing was changed.");
            if (confirmedCostPln is null)
                return new SessionResult(
                    $"Extending {Describe(session)} by {Text.Duration(minutes)} (until {Text.LocalTime(newEnd, time)}) costs {cost}. " +
                    $"Nothing was changed yet: confirm with the user, then call again with confirmedCostPln=" +
                    $"{cost.Amount.ToString("0.00", CultureInfo.InvariantCulture)}.", "quote", SessionId: session.Id, CostPln: cost.Amount);
            if (confirmedCostPln.Value != cost.Amount)
                return Refused("cost_mismatch",
                    $"The price changed: extending by {Text.Duration(minutes)} now costs {cost} (you confirmed " +
                    $"{confirmedCostPln.Value.ToString("0.00", CultureInfo.InvariantCulture)} PLN). Nothing was changed.");
        }

        await api.UpdateParkingDurationAsync(new UpdateParkingDurationRequest
        {
            SessionId = session.Id, NewEnd = newEnd, PaymentMethodId = card.Method!.Id,
        }, ct);
        var updated = (await api.GetSessionsAsync(ct)).FirstOrDefault(s => s.Id == session.Id) ?? session;
        return new SessionResult($"Extended: {Describe(updated)} ({(cost.IsZero ? "free" : cost.ToString())}).", "extended",
            SessionId: session.Id, CostPln: cost.Amount, DurationMinutes: minutes);
    }

    public async Task<SessionResult> EndAsync(string sessionId, CancellationToken ct)
    {
        var id = sessionId.Trim();
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.SessionId, id);
        var session = (await api.GetSessionsAsync(ct)).FirstOrDefault(s => s.Id == id);
        if (session is null)
            return Refused("session_not_found", "No running session has that id (it may have ended already). Call get_active_sessions for the current ones.");
        if (!session.SessionCanBeStopped)
            return Refused("not_stoppable", $"{Describe(session)} can't be stopped early; it ends by itself at its end time.");

        await api.StopParkingAsync(id, ct);

        // The bill can take a moment; post-pay sessions are charged now.
        SessionsHistoryDetails? billed = null;
        for (var attempt = 0; attempt < 3 && billed is null; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(2), time, ct);
            try
            {
                var details = await api.GetSessionsHistoryDetailsAsync(id, ct);
                if (!details.IsSessionActive) billed = details;
            }
            catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        }
        var name = session.Space?.Name ?? "the parking";
        if (billed is null)
            return new SessionResult($"Ended the session at {name} ({session.Vehicle?.LicensePlate}). The final cost isn't available yet; check get_session_history in a minute.",
                "stopped", SessionId: id);
        var cost = Money.TryParse(billed.TotalPrice, out var m) ? m : default;
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.CostPln, (double)cost.Amount);
        return new SessionResult($"Ended the session at {name} ({session.Vehicle?.LicensePlate}). Final cost: {cost}.", "stopped",
            SessionId: id, CostPln: cost.Amount);
    }

    /// <summary>The account's default payment method, if this server can pay with it.</summary>
    private async Task<(PaymentMethod? Method, SessionResult? Refusal)> DefaultCardAsync(string? spaceId, CancellationToken ct)
    {
        var methods = await api.GetPaymentMethodsAsync(spaceId, ct);
        var def = methods.FirstOrDefault(m => m.IsDefault);
        if (def is null)
            return (null, Refused("no_payment_method",
                "The account has no default payment method this parking accepts. Set a saved card as the default in the APCOA Flow app."));
        if (!IsSavedCard(def))
            return (null, Refused("default_not_a_card",
                $"The account's default payment method is {def.Type} {def.Identifier}, which this server can't charge without you " +
                "(it needs a saved card that's charged without any extra step). Set a saved card as the default in the APCOA Flow app."));
        return (def, null);
    }

    /// <summary>A card charged without interaction: <c>flow: saved</c>, no action, available, not a wallet.</summary>
    public static bool IsSavedCard(PaymentMethod m) =>
        m.Flow == "saved" && m.RequiredAction == "none" && m.Availability == "available" &&
        m.Type is not ("applePay" or "googlePay" or "unknown");

    private string Describe(Session s)
    {
        var end = s.EndDate is { } e ? $" until {Text.LocalTime(e, time)}" : "";
        return $"[{s.Id}] {s.Space?.Name ?? "unknown parking"} - {s.Vehicle?.LicensePlate}, from {Text.LocalTime(s.StartDate, time)}{end}";
    }

    private static DateTimeOffset Minute(DateTimeOffset t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, TimeSpan.Zero);

    private static SessionResult Refused(string outcome, string text, string? sessionId = null) => new(text, outcome, Error: true, SessionId: sessionId);

    private static SessionResult NotFree(string text, decimal? cost) => new(text, "not_free", CostPln: cost);
}
