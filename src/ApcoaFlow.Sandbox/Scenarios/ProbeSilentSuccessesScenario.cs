using ApcoaFlow.Sandbox.Dumping;
using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Models.Session;
using ApcoaFlowApiClient.ParkingData;
using ApcoaFlowMcp.Apcoa;
using Microsoft.Extensions.DependencyInjection;
using Refit;

namespace ApcoaFlow.Sandbox.Scenarios;

/// <summary>
/// WHAT: Probes the session endpoints end_session relies on for silent successes: stop an unknown session
/// id, then start a FREE-ONLY session on parking 616319 (unlimited free start/stop), stop it, and stop it a
/// second time. Writes only on 616319 and an id that doesn't exist; cannot charge (OnlyFree, max cost 0).
///
/// WHY: an unofficial API may answer 200 for ids it doesn't know (listonic did), which would make
/// end_session report "ended" for a session that never existed. The tool must say what really happened.
///
/// FOUND: see the dated notes in docs/API-NOTES.md ("Stopping a session").
/// </summary>
public sealed class ProbeSilentSuccessesScenario : IScenario
{
    public string Name => "probe-silent-successes";
    public string Description => "Stop unknown id; free start on 616319, stop, stop again (writes only on 616319, cannot charge).";

    private const int FreeParkingId = 616319;
    private const long UnknownSessionId = 2_000_000_000; // API ids are 32-bit ints

    public async Task RunAsync(IServiceProvider services, DumpRun run, CancellationToken ct)
    {
        var api = services.GetRequiredService<IApcoaFlowApi>();
        var sessions = new ApcoaFlowSessionService(api, services.GetRequiredService<IApcoaParkingDataSource>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ApcoaFlowSessionService>.Instance);

        await StopAsync(api, run, "unknown-id", UnknownSessionId, ct);

        var active = await api.GetParkingSessionsAsync(new ParkingSessionV2Query { Status = "ACTIVE", PageSize = 20 }, ct);
        if (active.Count > 0)
        {
            run.Log($"ABORT: {active.Count} session(s) already ACTIVE — not starting another. Ids: " +
                    string.Join(", ", active.Select(s => $"{s.Id} '{s.ParkingName}'")));
            return;
        }

        var consumer = await api.GetConsumerAsync(ct: ct);
        var plate = (consumer.VehicleInformationResponses.FirstOrDefault(v => v.IsDefault == 1)
                     ?? consumer.VehicleInformationResponses.First()).VehicleNumber;

        var result = await sessions.StartAsync(FreeParkingId, plate, durationMinutes: 15,
            ApcoaFlowSessionService.StartMode.OnlyFree, confirmedCostPln: null, maxCostPln: 0m, bypassCap: false, ct);
        run.Log($"free start on {FreeParkingId}: started={result.Started} cost={result.CostPln:0.00} msg={result.Message}");
        if (!result.Started || result.Session is not { } started)
            return;

        var sessionId = started.ParkingSession?.Id ?? started.Id;
        await StopAsync(api, run, "first-stop", sessionId, ct);
        await StopAsync(api, run, "second-stop", sessionId, ct);

        var completed = await api.GetParkingSessionsAsync(new ParkingSessionV2Query { Status = "COMPLETED", PageSize = 5 }, ct);
        var record = completed.FirstOrDefault(s => s.Id == sessionId);
        run.Log($"completed record: {(record is null ? "MISSING" : $"status={record.ParkingSessionStatus} amount={record.Amount:0.00}")}");
    }

    private static async Task StopAsync(IApcoaFlowApi api, DumpRun run, string label, long id, CancellationToken ct)
    {
        try
        {
            var r = await api.StopParkingSessionAsync(new StopParkingSessionRequest
            {
                ParkingSessionId = id,
                SessionCheckOutTime = DateTimeOffset.UtcNow,
            }, ct);
            run.Log($"{label} ({id}): 2xx id={r?.Id} status={r?.ParkingSessionStatus} amount={r?.Amount}");
        }
        catch (ApiException ex)
        {
            run.Log($"{label} ({id}): HTTP {(int)ex.StatusCode} body={ex.Content}");
        }
    }
}
