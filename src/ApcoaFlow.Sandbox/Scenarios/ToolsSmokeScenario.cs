using ApcoaFlow.Sandbox.Dumping;
using ApcoaFlowApiClient;
using ApcoaFlowApiClient.ParkingData;
using ApcoaFlowMcp.Apcoa;
using ApcoaFlowMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ApcoaFlow.Sandbox.Scenarios;

/// <summary>
/// WHAT: READ-ONLY. Runs the MCP tool layer (<see cref="ApcoaFlowTools"/>) rather than the raw API:
/// find_parking around parking 616319 (Tarnowskie Góry), get_registered_cars, get_active_sessions and
/// get_session_history, printing the exact tool output and the tools' own log lines into run.log.
/// Starts / extends / stops NOTHING (no start_* tool is called — even a free-only start can start a session).
///
/// WHY: separates "the API returned something odd" from "the tool formatted it oddly" — the dumps show the
/// wire, run.log shows what the model would see (and what the server logs for each call).
///
/// FOUND (2026-11-27): all four read tools format as documented in docs/MCP-TOOLS.md; each writes one
/// structured log line per call.
/// </summary>
public sealed class ToolsSmokeScenario : IScenario
{
    public string Name => "tools-smoke";
    public string Description => "Read-only: find_parking, get_registered_cars, get_active_sessions, get_session_history through the MCP tool layer.";

    // Parking 616319 — Tarnowskie Góry ul. Oświęcimska.
    private const double Lat = 50.44333;
    private const double Lon = 18.86299;

    public async Task RunAsync(IServiceProvider services, DumpRun run, CancellationToken ct)
    {
        var api = services.GetRequiredService<IApcoaFlowApi>();
        var data = services.GetRequiredService<IApcoaParkingDataSource>();
        var tools = new ApcoaFlowTools(
            new ApcoaFlowSessionService(api, data, NullLogger<ApcoaFlowSessionService>.Instance),
            data, Options.Create(new ApcoaFlowToolsOptions()), new RunLogger(run));

        run.Log("find_parking (500 m around 616319) →\n" + await tools.FindParkingAsync(Lat, Lon, 500, ct: ct));
        run.Log("find_parking (2 km, \"Aldi\") →\n" + await tools.FindParkingAsync(Lat, Lon, 2000, "Aldi", ct));
        run.Log("get_registered_cars →\n" + await tools.GetRegisteredCarsAsync(ct));
        run.Log("get_active_sessions →\n" + await tools.GetActiveSessionsAsync(ct));
        run.Log("get_session_history →\n" + await tools.GetSessionHistoryAsync(ct));
    }

    /// <summary>Writes the tools' log lines into run.log, next to their output.</summary>
    private sealed class RunLogger(DumpRun run) : ILogger<ApcoaFlowTools>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => run.Log($"  [tool log {logLevel}] {formatter(state, exception)}");
    }
}
