using ApcoaFlow.Sandbox.Dumping;
using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Models.Session;
using Microsoft.Extensions.DependencyInjection;

namespace ApcoaFlow.Sandbox.Scenarios;

/// <summary>
/// WHAT: READ-ONLY. Fetches the consumer profile and lists ACTIVE / COMPLETED / IN_REVIEW sessions,
/// dumping every request/response in full. Starts / extends / stops NOTHING.
///
/// WHY: the first, always-safe scenario — it proves the dump pipeline end-to-end and gives a quick
/// snapshot of the account (plates, payment methods, recent session history and their charges).
///
/// FOUND / USE IT FOR: confirming whether a recent start actually charged (COMPLETED amount), whether
/// anything is stuck ACTIVE, and as the template to copy when writing a new read-only probe.
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
