using ApcoaFlow.Sandbox.Dumping;
using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Models.Session;
using Microsoft.Extensions.DependencyInjection;

namespace ApcoaFlow.Sandbox.Scenarios;

/// <summary>
/// READ-ONLY. Fetches the consumer profile and lists ACTIVE / COMPLETED / IN_REVIEW sessions.
/// Starts / extends / stops NOTHING, so it is always safe to run. Doubles as the reference
/// scenario proving the dump pipeline works end-to-end.
/// </summary>
public sealed class DumpSessionsScenario : IScenario
{
    public string Name => "dump-sessions";
    public string Description => "Read-only: consumer profile + ACTIVE/COMPLETED/IN_REVIEW sessions.";

    public async Task RunAsync(IServiceProvider services, DumpRun run, CancellationToken ct)
    {
        var api = services.GetRequiredService<IApcoaFlowApi>();

        var consumer = await api.GetConsumerAsync(ct: ct);
        run.Log($"consumer id={consumer.Id} profile={consumer.DefaultProfileId} " +
                $"vehicles={consumer.VehicleInformationResponses.Count} " +
                $"paymentMethods={consumer.ConsumerUserPaymentMethods.Count}");

        foreach (var status in new[] { "ACTIVE", "COMPLETED", "IN_REVIEW" })
        {
            var list = await api.GetParkingSessionsAsync(
                new ParkingSessionV2Query { Status = status, PageSize = 15 }, ct);
            run.Log($"{status}: {list.Count} session(s)");
            foreach (var s in list)
                run.Log($"  id={s.Id} '{s.ParkingName}' [{s.ParkingId}] status={s.ParkingSessionStatus} " +
                        $"amount={s.Amount:0.00} {s.CurrencyCode}");
        }
    }
}
