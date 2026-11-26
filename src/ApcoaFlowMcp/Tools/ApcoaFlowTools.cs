using System.ComponentModel;
using System.Text;
using ApcoaFlowApiClient.Models.Session;
using ApcoaFlowMcp.Apcoa;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace ApcoaFlowMcp.Tools;

// NOTE: docs/MCP-TOOLS.md is the human-facing reference for these tools (names, inputs, example
// outputs). When you add/rename a tool or change its parameters, behaviour, or output format,
// update docs/MCP-TOOLS.md in the same change to keep it accurate.
[McpServerToolType]
public sealed class ApcoaFlowTools(
    ApcoaFlowSessionService sessions,
    ApcoaFlowApiClient.ParkingData.IApcoaParkingDataSource parkingData,
    IOptions<ApcoaFlowToolsOptions> options)
{
    private readonly ApcoaFlowToolsOptions _options = options.Value;

    [McpServerTool(Name = "find_parking")]
    [Description("Find APCOA parkings near a location, optionally filtered by name (e.g. \"Carrefour\", \"Lidl\"). Returns one line per parking as \"[id] name - address (distance m)\", nearest first. Use the id with start_session.")]
    public async Task<string> FindParkingAsync(
        [Description("Latitude of the search center (decimal degrees)")] double latitude,
        [Description("Longitude of the search center (decimal degrees)")] double longitude,
        [Description("Search radius in metres. Keep small (e.g. 1000-2000) for 'near me' queries.")] double radiusMeters = 2000,
        [Description("Optional name filter, e.g. a retail brand like \"Carrefour\" or \"Biedronka\". Case- and accent-insensitive.")] string? nameContains = null,
        CancellationToken ct = default)
    {
        var results = await parkingData.FindNearbyAsync(latitude, longitude, radiusMeters / 1000.0, nameContains, ct);
        if (results.Count == 0)
            return "No parkings found for the given location/filter.";

        var sb = new StringBuilder();
        foreach (var (p, distanceKm) in results)
        {
            var addr = string.IsNullOrWhiteSpace(p.Address) ? "" : $" - {p.Address}";
            var meters = (int)Math.Round(distanceKm * 1000);
            sb.AppendLine($"[{p.Id}] {p.DisplayName}{addr} ({meters} m)");
        }
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "get_registered_cars")]
    [Description("List the license plates registered on the account. Returns one line per car as \"plate [default] - status\".")]
    public async Task<string> GetRegisteredCarsAsync(CancellationToken ct = default)
    {
        var cars = await sessions.GetVehiclesAsync(ct);
        if (cars.Count == 0)
            return "No registered cars on the account.";

        var sb = new StringBuilder();
        foreach (var c in cars)
        {
            var def = c.IsDefault == 1 ? " [default]" : "";
            var status = string.IsNullOrWhiteSpace(c.IdentificationStatus) ? "" : $" - {c.IdentificationStatus}";
            sb.AppendLine($"{c.VehicleNumber}{def}{status}");
        }
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "get_active_sessions")]
    [Description("Returns the currently active parking session(s), or a message if none is running. You don't need to call this before starting — start_session detects an already-active session itself — but it's useful to inspect running sessions or get an id to end.")]
    public async Task<string> GetActiveSessionsAsync(CancellationToken ct = default)
    {
        var active = await sessions.GetActiveAsync(ct);
        if (active.Count == 0)
            return "No active parking session.";
        return string.Join("\n", active.Select(FormatSession));
    }

    [McpServerTool(Name = "get_session_history")]
    [Description("Returns the user's recent completed parking sessions, most recent first, with cost and duration.")]
    public async Task<string> GetSessionHistoryAsync(CancellationToken ct = default)
    {
        var history = await sessions.GetHistoryAsync(ct);
        if (history.Count == 0)
            return "No past parking sessions.";
        return string.Join("\n", history.Select(FormatSession));
    }

    [McpServerTool(Name = "start_session")]
    [Description(
        "Start a parking session at a parking (by id, from find_parking) for a registered license plate. " +
        "May cost money. FREE sessions start immediately. PAID sessions do NOT start on the first call: the " +
        "tool returns the SHORTEST available duration and its price, plus the other price options, and a note " +
        "on whether stopping early refunds. To start, call again with BOTH durationMinutes (the option you " +
        "chose) and confirmedCostPln set to EXACTLY that option's price (it must match the live cost, or the " +
        "start is refused). Enforces a maximum cost limit. Always confirm parking, plate, duration, and cost " +
        "with the user before confirming a paid session. (To guarantee a free-only start, use start_free_session.)")]
    public async Task<string> StartSessionAsync(
        [Description("Parking id (from find_parking)")] int parkingId,
        [Description("Registered license plate, e.g. \"ABC 123DE\" (see get_registered_cars)")] string licensePlate,
        [Description("Desired duration in minutes. Omit to use the server's default. Only rounded up to a free-time promotion's window when that promotion actually validates as usable; otherwise your exact requested duration is quoted.")] int? durationMinutes = null,
        [Description("For a PAID session: the exact cost in PLN you are confirming (the chosen option's price from a prior call's quote). Must match the live cost exactly. Omit on the first call.")] decimal? confirmedCostPln = null,
        CancellationToken ct = default)
    {
        var result = await sessions.StartAsync(
            parkingId, licensePlate, durationMinutes, ApcoaFlowSessionService.StartMode.Normal,
            confirmedCostPln, _options.MaxSessionCostPln, bypassCap: false, ct);
        return FormatStart(result);
    }

    [McpServerTool(Name = "start_free_session")]
    [Description(
        "Start a FREE parking session at a parking (by id, from find_parking) for a registered license plate. " +
        "Auto-applies an active free-time promotion and starts only if the session is actually free (cost 0); " +
        "if it would cost anything, nothing is started and the response instead reports the price options and " +
        "suggests using start_session for a paid session. Safe: this tool never incurs a charge.")]
    public async Task<string> StartFreeSessionAsync(
        [Description("Parking id (from find_parking)")] int parkingId,
        [Description("Registered license plate, e.g. \"ABC 123DE\" (see get_registered_cars)")] string licensePlate,
        [Description("Desired duration in minutes. Omit to use the full free window.")] int? durationMinutes = null,
        CancellationToken ct = default)
    {
        var result = await sessions.StartAsync(
            parkingId, licensePlate, durationMinutes, ApcoaFlowSessionService.StartMode.OnlyFree,
            confirmedCostPln: null, _options.MaxSessionCostPln, bypassCap: false, ct);
        return FormatStart(result);
    }

    [McpServerTool(Name = "start_session_current_location")]
    [Description(
        "Start a parking session at the closest parking to the user's current GPS location (within a " +
        "search radius), for a registered license plate. May cost money. Same FREE/PAID behaviour as " +
        "start_session: free starts immediately; paid returns the shortest option + the other price " +
        "options + a cancel/refund note, and needs a second call with the chosen durationMinutes and " +
        "confirmedCostPln set to EXACTLY that option's cost. Use this when the user wants to park " +
        "'here'/'where I am'. (To guarantee a free-only start here, use start_free_session_current_location.)")]
    public async Task<string> StartSessionCurrentLocationAsync(
        [Description("Your current latitude (decimal degrees)")] double latitude,
        [Description("Your current longitude (decimal degrees)")] double longitude,
        [Description("Registered license plate, e.g. \"ABC 123DE\" (see get_registered_cars)")] string licensePlate,
        [Description("Desired duration in minutes. Omit to use the server's default duration. If a free-time promotion applies and this is shorter than the free window, it is rounded up to the full free window so none of the free time is wasted.")] int? durationMinutes = null,
        [Description("Search radius in metres for the closest parking. Omit to use the server's configured default.")] double? radiusMeters = null,
        [Description("For a PAID session: the exact cost in PLN you are confirming (from a prior call's quote). Omit on the first call.")] decimal? confirmedCostPln = null,
        CancellationToken ct = default)
    {
        var result = await sessions.StartAtLocationAsync(
            latitude, longitude, licensePlate, durationMinutes, ApcoaFlowSessionService.StartMode.Normal,
            confirmedCostPln, _options.MaxSessionCostPln, bypassCap: false,
            radiusMeters ?? _options.CurrentLocationRadiusMeters, ct);
        return FormatStart(result);
    }

    [McpServerTool(Name = "start_free_session_current_location")]
    [Description(
        "Start a FREE parking session at the closest parking to the user's current GPS location (within a " +
        "search radius), for a registered license plate. Auto-applies an active free-time promotion and " +
        "starts only if the session is actually free; if it would cost anything nothing is started and the " +
        "response reports the price options and suggests start_session_current_location for a paid session. " +
        "Safe: never incurs a charge. Use this for 'park free here/where I am'.")]
    public async Task<string> StartFreeSessionCurrentLocationAsync(
        [Description("Your current latitude (decimal degrees)")] double latitude,
        [Description("Your current longitude (decimal degrees)")] double longitude,
        [Description("Registered license plate, e.g. \"ABC 123DE\" (see get_registered_cars)")] string licensePlate,
        [Description("Desired duration in minutes. Omit to use the full free window.")] int? durationMinutes = null,
        [Description("Search radius in metres for the closest parking. Omit to use the server's configured default.")] double? radiusMeters = null,
        CancellationToken ct = default)
    {
        var result = await sessions.StartAtLocationAsync(
            latitude, longitude, licensePlate, durationMinutes, ApcoaFlowSessionService.StartMode.OnlyFree,
            confirmedCostPln: null, _options.MaxSessionCostPln, bypassCap: false,
            radiusMeters ?? _options.CurrentLocationRadiusMeters, ct);
        return FormatStart(result);
    }

    [McpServerTool(Name = "start_expensive_session")]
    [Description("Does the same as start_session but bypasses the maximum-cost limit.")]
    public async Task<string> StartExpensiveSessionAsync(
        [Description("Parking id (from find_parking)")] int parkingId,
        [Description("Registered license plate, e.g. \"ABC 123DE\"")] string licensePlate,
        [Description("Desired duration in minutes. Omit to use the server's default duration. If a free-time promotion applies and this is shorter than the free window, it is rounded up to the full free window so none of the free time is wasted.")] int? durationMinutes = null,
        [Description("The exact cost in PLN you are confirming (from a prior call's quote). Must match the live cost exactly. Omit on the first call.")] decimal? confirmedCostPln = null,
        CancellationToken ct = default)
    {
        var result = await sessions.StartAsync(
            parkingId, licensePlate, durationMinutes,
            ApcoaFlowSessionService.StartMode.Normal,
            confirmedCostPln, _options.MaxSessionCostPln, bypassCap: true, ct);
        return FormatStart(result);
    }

    [McpServerTool(Name = "end_session")]
    [Description("End an active parking session by its id (from get_active_sessions). Returns the final cost. Always confirm with the user before calling.")]
    public async Task<string> EndSessionAsync(
        [Description("The session id (from get_active_sessions)")] long sessionId,
        CancellationToken ct = default)
    {
        var ended = await sessions.StopAsync(sessionId, ct);
        if (ended is null)
            return $"Session {sessionId} was ended (no completed record returned to confirm cost).";
        return $"Ended session {sessionId}: {ended.Amount:0.00} {ended.CurrencyCode} at {ended.ParkingName}.";
    }

    // -------------------------------------------------------------------------

    private static string FormatStart(ApcoaFlowSessionService.StartResult r)
    {
        var sb = new StringBuilder();

        // Always surface which parking (name + id) and, when started via location, how far away it is.
        if (r.ParkingName is not null || r.ParkingId != 0)
        {
            sb.Append($"Parking: {r.ParkingName ?? "?"} [{r.ParkingId}]");
            if (r.DistanceMeters is int d)
                sb.Append($", {d} m away");
            sb.Append(". ");
        }

        sb.Append(r.Message);

        // Cancel/refund note — useful before confirming a paid start, and after starting (so the user
        // knows whether finishing early would refund). Appended to both quote and started results.
        if (!string.IsNullOrEmpty(r.CancelRefundNote))
            sb.Append(' ').Append(r.CancelRefundNote);

        if (!r.Started)
            return sb.ToString();

        if (r.Session is { } s)
            sb.Append($" Session id: {s.SessionId}, status: {s.ParkingSessionStatus}.");
        if (r.PromotionName is not null)
            sb.Append($" Promotion applied: {r.PromotionName}" + (r.FreeMinutes is int m ? $" ({m} free min)." : "."));
        if (r.PromotionReuseCooldown is { } cooldown && cooldown > TimeSpan.Zero)
            sb.Append($" This coupon is now used up and can be used again in {FormatDuration(cooldown)}.");
        return sb.ToString();
    }

    /// <summary>
    /// Formats a reuse cooldown in the largest natural units: "4 hours", "90 minutes" → "1 hour 30 minutes",
    /// "2 days". Days and hours combine with a trailing remainder; sub-minute amounts fall back to seconds.
    /// </summary>
    internal static string FormatDuration(TimeSpan d)
    {
        static string Unit(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";

        if (d.TotalMinutes < 1)
            return Unit(Math.Max(1, (int)Math.Round(d.TotalSeconds)), "second");

        var parts = new List<string>();
        var days = (int)d.TotalDays;
        if (days > 0) parts.Add(Unit(days, "day"));
        if (d.Hours > 0) parts.Add(Unit(d.Hours, "hour"));
        if (d.Minutes > 0) parts.Add(Unit(d.Minutes, "minute"));
        return string.Join(" ", parts);
    }

    private static string FormatSession(ParkingSessionV2 s)
    {
        var status = $"[{s.ParkingSessionStatus.ToLowerInvariant()}]";
        var when = (s.CheckinTime ?? s.ScheduledCheckinTime)?.ToString("u") ?? "";
        var cost = $"{s.Amount:0.00} {s.CurrencyCode}";
        return $"{status} [{s.Id}] {s.ParkingName} plate:{s.LicensePlate} {cost} since:{when}";
    }
}
