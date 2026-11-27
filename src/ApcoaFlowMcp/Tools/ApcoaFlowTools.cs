using System.ComponentModel;
using System.Text;
using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Api;
using ModelContextProtocol.Server;

namespace ApcoaFlowMcp.Tools;

/// <summary>
/// The MCP tools. Each public method with <c>[McpServerTool]</c> is registered by <see cref="ToolRegistration"/>
/// (a fresh instance per call, from the request's services). Names follow the APCOA Flow API (vehicles, spaces,
/// sessions). More tools follow in port plan M2–M4.
/// </summary>
[McpServerToolType]
public sealed class ApcoaFlowTools(IApcoaFlowApi api, ILogger<ApcoaFlowTools> logger)
{
    [McpServerTool(Name = "get_vehicles", Title = "Get vehicles", ReadOnly = true, Idempotent = true)]
    [Description("List the vehicles (license plates) on the account. Returns one line per vehicle as " +
                 "\"plate [default] - description\".")]
    public Task<string> GetVehiclesAsync(CancellationToken ct = default)
        => ToolErrors.GuardAsync("get_vehicles", logger, async () =>
    {
        using var _ = logger.BeginScope("{Tool}", "get_vehicles");
        var vehicles = await api.GetVehiclesAsync(ct);
        logger.LogInformation("get_vehicles: {Count} vehicle(s) [{Plates}]",
            vehicles.Count, string.Join(", ", vehicles.Select(v => v.LicensePlate)));
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ResultsCount, vehicles.Count);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.LicensePlates, ApcoaTelemetry.Capped(vehicles.Select(v => v.LicensePlate)));
        ApcoaTelemetry.Outcome("ok");
        if (vehicles.Count == 0)
            return "No vehicles on the account.";

        var sb = new StringBuilder();
        foreach (var v in vehicles)
        {
            var isDefault = v.IsDefault ? " [default]" : "";
            var description = string.IsNullOrWhiteSpace(v.Description) ? "" : $" - {v.Description}";
            sb.AppendLine($"{v.LicensePlate}{isDefault}{description}");
        }
        return sb.ToString().TrimEnd();
    });
}
