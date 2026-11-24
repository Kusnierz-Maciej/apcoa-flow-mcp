using System.ComponentModel;
using ApcoaFlowApiClient.Models.Parking;
using ApcoaFlowMcp.Apcoa;
using ModelContextProtocol.Server;

namespace ApcoaFlowMcp.Tools;

[McpServerToolType]
public sealed class ApcoaFlowTools(ApcoaFlowApiAccessor accessor)
{
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
