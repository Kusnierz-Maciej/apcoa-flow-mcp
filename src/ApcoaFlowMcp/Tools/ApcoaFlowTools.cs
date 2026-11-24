using System.ComponentModel;
using System.Text;
using ApcoaFlowApiClient.Models.Parking;
using ApcoaFlowApiClient.ParkingData;
using ApcoaFlowMcp.Apcoa;
using ModelContextProtocol.Server;

namespace ApcoaFlowMcp.Tools;

// 'accessor' is injected for the API-backed session tools below, which are still stubbed
// (NotImplementedException). Suppress the "unread parameter" warning until they are implemented.
#pragma warning disable CS9113
[McpServerToolType]
public sealed class ApcoaFlowTools(ApcoaFlowApiAccessor accessor, IApcoaParkingDataSource parkingData)
{
    [McpServerTool(Name = "find_parking")]
    [Description("Find APCOA parkings near a location, optionally filtered by name (e.g. \"Carrefour\", \"Lidl\"). Returns one line per parking as \"[id] name - address (distance km)\", nearest first. Use the id with other parking tools.")]
    public async Task<string> FindParkingAsync(
        [Description("Latitude of the search center (decimal degrees)")] double latitude,
        [Description("Longitude of the search center (decimal degrees)")] double longitude,
        [Description("Search radius in kilometers. Keep small (e.g. 1-2) for 'near me' queries.")] double radiusKm = 2.0,
        [Description("Optional name filter, e.g. a retail brand like \"Carrefour\" or \"Biedronka\". Case- and accent-insensitive.")] string? nameContains = null,
        CancellationToken ct = default)
    {
        var results = await parkingData.FindNearbyAsync(latitude, longitude, radiusKm, nameContains, ct);
        if (results.Count == 0)
            return "No parkings found for the given location/filter.";

        var sb = new StringBuilder();
        foreach (var (p, distanceKm) in results)
        {
            var addr = string.IsNullOrWhiteSpace(p.Address) ? "" : $" - {p.Address}";
            var city = string.IsNullOrWhiteSpace(p.City) ? "" : $", {p.City}";
            sb.AppendLine($"[{p.Id}] {p.DisplayName}{addr}{city} ({distanceKm:F1} km)");
        }
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "get_parking_zones")]
    [Description("List all available parking zones. Returns one line per zone as \"[zoneId] name - address\". Call this first to get a zoneId before starting a session.")]
    public async Task<string> GetParkingZonesAsync(CancellationToken ct = default)
    {
        // TODO: implement once API is known
        throw new NotImplementedException("get_parking_zones is not yet implemented.");
    }

    [McpServerTool(Name = "get_active_session")]
    [Description("Returns the currently active parking session, or a message if no session is running. Always call this before start_session to avoid duplicate sessions.")]
    public async Task<string> GetActiveSessionAsync(CancellationToken ct = default)
    {
        // TODO: implement once API is known
        throw new NotImplementedException("get_active_session is not yet implemented.");
    }

    [McpServerTool(Name = "start_session")]
    [Description("Start a parking session at the given zone for the given license plate. Always confirm zone name and license plate with the user before calling. Returns the new session details.")]
    public async Task<string> StartSessionAsync(
        [Description("The zone ID (from get_parking_zones)")] string zoneId,
        [Description("The zone name exactly as get_parking_zones shows it. Shown in the permission prompt; not sent to the API.")] string zoneName,
        [Description("License plate number, e.g. \"WA12345\"")] string licensePlate,
        CancellationToken ct = default)
    {
        // TODO: implement once API is known
        throw new NotImplementedException("start_session is not yet implemented.");
    }

    [McpServerTool(Name = "end_session")]
    [Description("End the active parking session. Always confirm with the user before calling. Returns the ended session details including duration.")]
    public async Task<string> EndSessionAsync(
        [Description("The session ID (from get_active_session)")] string sessionId,
        CancellationToken ct = default)
    {
        // TODO: implement once API is known
        throw new NotImplementedException("end_session is not yet implemented.");
    }

    [McpServerTool(Name = "get_session_history")]
    [Description("Returns the user's past parking sessions in reverse chronological order.")]
    public async Task<string> GetSessionHistoryAsync(CancellationToken ct = default)
    {
        // TODO: implement once API is known
        throw new NotImplementedException("get_session_history is not yet implemented.");
    }

    private static string FormatSession(ParkingSession s)
    {
        var status = s.IsActive ? "[active]" : "[ended]";
        var duration = s.EndedAt.HasValue
            ? $" ({(s.EndedAt.Value - s.StartedAt).TotalMinutes:F0} min)"
            : "";
        return $"{status} [{s.Id}] zone:{s.ZoneId} plate:{s.LicensePlate} started:{s.StartedAt:g}{duration}";
    }
}
#pragma warning restore CS9113
