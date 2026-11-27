using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Http;
using ApcoaFlowApiClient.Models;
using ApcoaFlow.Sandbox.Dumping;
using Microsoft.Extensions.DependencyInjection;
using Refit;

namespace ApcoaFlow.Sandbox.Scenarios;

/// <summary>
/// WHAT: read-only scan for cheap parkings that charge RIGHT NOW: dated quotes (1, 15 and 60 min) for the first vehicle
/// at a list of candidates found by search, with how they charge (pre-pay at start or post-pay at stop). Nothing starts.
/// WHY: the paid-path checks (post-pay grace period, one real payment) need a cheap post-pay parking that isn't free
/// at that moment; municipal paid zones are often free at night and at weekends.
/// FOUND (2026-11-27, Friday 22:00): 22 of 44 free; cheapest charging: Przy Ratuszu Warszawa Górczewska 224 PLN 0.40
/// (pre-pay), Markinvest Warszawa al. Wilanowska 363 PLN 1.00 (pre-pay); the only post-pay one charging was Katowice
/// ul. Sobieskiego 23 (PLN 5.00 minimum). Czarny Dunajec was free.
/// </summary>
public sealed class PaidCandidatesScenario : IScenario
{
    public string Name => "paid-candidates";
    public string Description => "Cheap parkings that charge right now, pre- vs post-pay (quotes only). Read-only.";

    private static readonly string[] Queries =
    [
        "Czarny Dunajec", "Markinvest", "Przy Ratuszu", "Kamienna 4", "Aero Klub", "Katowice", "Bytom", "Rybnik",
        "Warszawa", "Kraków", "Wrocław", "Gdańsk", "Poznań",
    ];

    public async Task RunAsync(IServiceProvider services, DumpRun run, CancellationToken ct)
    {
        var api = services.GetRequiredService<IApcoaFlowApi>();
        var vehicle = (await api.GetVehiclesAsync(ct)).First();
        var seen = new HashSet<string>();
        var now = DateTimeOffset.UtcNow;
        var rows = new List<(decimal OneHour, string Line)>();

        foreach (var query in Queries)
        {
            var hits = (await api.SearchAsync(new SearchQuery { Query = query, Size = 10, IsParking = true, IsCharging = false }, ct)).Spaces;
            foreach (var hit in hits.Where(h => seen.Add(h.Id)).Take(6))
            {
                try
                {
                    async Task<PriceQuote> Quote(int minutes) => await api.GetPriceAsync(new PriceQuery
                    {
                        SpaceId = hit.Id, LicensePlateId = vehicle.Id, Duration = minutes * 60,
                        StartDate = UtcMillisecondsConverter.ToWire(now),
                    }, ct);
                    var one = await Quote(1);
                    var fifteen = await Quote(15);
                    var hour = await Quote(60);
                    rows.Add((hour.Total.Amount,
                        $"{hit.Id} '{hit.Name}' code={hit.TariffCode}: 1 min {one.TotalPrice}, 15 min {fifteen.TotalPrice}, " +
                        $"1 h {hour.TotalPrice}; {(hour.PrepaymentMode == "autoCharge" ? "PRE-pay (charged at start)" : "POST-pay (charged at stop)")}"));
                }
                catch (ApiException ex)
                {
                    run.Log($"{hit.Id} '{hit.Name}': HTTP {(int)ex.StatusCode} {ApcoaFlowApiError.TryRead(ex)?.Code}");
                }
            }
        }

        run.Log($"--- {rows.Count} parkings quoted at {now:u}, cheapest paying first ---");
        foreach (var (_, line) in rows.Where(r => r.OneHour > 0).OrderBy(r => r.OneHour))
            run.Log(line);
        run.Log($"--- free right now: {rows.Count(r => r.OneHour == 0)} ---");
    }
}
