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
        "tool returns the cost, and you must confirm by calling again with confirmedCostPln set to EXACTLY " +
        "that cost (it must match the live cost precisely, or the start is refused). Enforces a maximum cost " +
        "limit. Always confirm parking, plate, and cost with the user before confirming a paid session. " +
        "(To guarantee a free-only start, use start_free_session instead.)")]
    public async Task<string> StartSessionAsync(
        [Description("Parking id (from find_parking)")] int parkingId,
        [Description("Registered license plate, e.g. \"ABC 123DE\" (see get_registered_cars)")] string licensePlate,
        [Description("Desired duration in minutes. Omit to use the server's default duration. If a free-time promotion applies and this is shorter than the free window, it is rounded up to the full free window so none of the free time is wasted.")] int? durationMinutes = null,
        [Description("For a PAID session: the exact cost in PLN you are confirming (from a prior call's quote). Must match the live cost exactly. Omit on the first call.")] decimal? confirmedCostPln = null,
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
        "if it would cost anything, it is refused and nothing is started. Safe: this tool never incurs a charge. " +
        "Use start_session for anything that may be paid.")]
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
        "start_session: free starts immediately; paid returns the cost and needs a second call with " +
        "confirmedCostPln set to EXACTLY that cost. Use this when the user wants to park 'here'/'where I am'. " +
        "(To guarantee a free-only start here, use start_free_session_current_location.)")]
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
        "starts only if the session is actually free; refused (nothing started) if it would cost anything. Safe: " +
        "never incurs a charge. Use this for 'park free here/where I am'.")]
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

        if (!r.Started)
            return sb.ToString();

        if (r.Session is { } s)
            sb.Append($" Session id: {s.SessionId}, status: {s.ParkingSessionStatus}.");
        if (r.PromotionName is not null)
            sb.Append($" Promotion applied: {r.PromotionName}" + (r.FreeMinutes is int m ? $" ({m} free min)." : "."));
        return sb.ToString();
    }

    private static string FormatSession(ParkingSessionV2 s)
    {
        var status = $"[{s.ParkingSessionStatus.ToLowerInvariant()}]";
        var when = (s.CheckinTime ?? s.ScheduledCheckinTime)?.ToString("u") ?? "";
        var cost = $"{s.Amount:0.00} {s.CurrencyCode}";
        return $"{status} [{s.Id}] {s.ParkingName} plate:{s.LicensePlate} {cost} since:{when}";
    }
}
