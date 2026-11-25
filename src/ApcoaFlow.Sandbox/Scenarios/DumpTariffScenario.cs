using ApcoaFlow.Sandbox.Dumping;
using ApcoaFlowApiClient;
using Microsoft.Extensions.DependencyInjection;
using Refit;

namespace ApcoaFlow.Sandbox.Scenarios;

/// <summary>
/// WHAT: READ-ONLY. Dumps the raw GET /tariff?parkingLotId=... response for a lot (default Carrefour
/// TG lot 10584) and logs the decoded pricing grid. Starts nothing.
///
/// WHY: to settle whether the API exposes a pricing-per-length structure we could derive exact tier
/// boundaries from, instead of probing tariffBreakDownV2 at many durations.
///
/// FOUND (2026-11-25): YES — /tariff returns the full grid (tariffTemplates → stacks → slots → grids:
/// FLAT + INCREMENTAL, plus DAY maxima), which the old thin TariffInfo model failed to map. UNIT GOTCHA:
/// grid amountInCents is in 1/100 of a cent — divide by 10000 for PLN (Carrefour FLAT 20000/60min =
/// 2.00 PLN, INCREMENTAL 40000/60min = +4.00 PLN/h, DAY 500000 = 50 PLN/day). The price ladder is now
/// derived from this. See memory: project_tariff_endpoint_structure.
/// </summary>
public sealed class DumpTariffScenario : IScenario
{
    public string Name => "dump-tariff";
    public string Description => "Read-only: raw GET /tariff for a lot (default 10584) to inspect any rate-row structure.";

    private const int DefaultLotId = 10584;

    public async Task RunAsync(IServiceProvider services, DumpRun run, CancellationToken ct)
    {
        var api = services.GetRequiredService<IApcoaFlowApi>();

        // Resolve the lot for Carrefour 9882 so we also log its tariff id, then dump raw /tariff.
        var lots = await api.GetParkingLotsAsync(9882, ct: ct);
        var lot = lots.FirstOrDefault(l => l.LotId is not null);
        var lotId = lot?.LotId ?? DefaultLotId;
        run.Log($"lot={lotId} tariff={lot?.PrimaryTariffId} name='{lot?.NearbyParkingLot?.Name}'");

        try
        {
            var resp = await api.GetTariffAsync(lotId, ct);
            var tmpl = resp.TariffTemplates.FirstOrDefault(t => t.IsActive == 1) ?? resp.TariffTemplates.FirstOrDefault();
            run.Log($"GetTariff OK: {resp.TariffTemplates.Count} template(s). " +
                    (tmpl is null ? "(none)" :
                     $"default id={tmpl.TariffId} name='{tmpl.Name}' proration={tmpl.IsProrationAllowed} " +
                     $"freeMin={tmpl.FreeDurationInMinutes} stacks={tmpl.Stacks.Count} " +
                     $"grids=[{string.Join(", ", tmpl.Stacks.SelectMany(s => s.Slots).SelectMany(sl => sl.Grids).Select(g => $"{g.Type}:{g.AmountInCents}/{g.Duration}min"))}] " +
                     $"maxima=[{string.Join(", ", tmpl.Stacks.SelectMany(s => s.Maxima).Select(m => $"{m.Type}:{m.AmountInCents}"))}]"));
        }
        catch (ApiException ex)
        {
            run.Log($"GetTariff failed HTTP {(int)ex.StatusCode}: {ex.Content}");
        }

        run.Log("dump-tariff complete — read the GET tariff .http file for the FULL raw body (fields our model may drop).");
    }
}
