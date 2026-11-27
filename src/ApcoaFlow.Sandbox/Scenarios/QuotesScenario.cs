using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Http;
using ApcoaFlowApiClient.Models;
using ApcoaFlow.Sandbox.Dumping;
using Microsoft.Extensions.DependencyInjection;
using Refit;

namespace ApcoaFlow.Sandbox.Scenarios;

/// <summary>
/// WHAT: price quotes for the first registered vehicle (read-only, nothing starts): a duration ladder on the test
/// space Ostrów and on a Biedronka (free window, minimum charge, whether <c>maxDuration</c> is minutes or seconds),
/// both ids that <c>search</c> returned for the same Biedronka, one quote with a made-up promo code, the duration
/// presets (with a timeout: it hung once) and the account's benefits.
/// WHY: the start tools decide "free" from the quote, find the free window from it, and may take a promo code.
/// FOUND (2026-11-27, Friday evening): Ostrów 1 min–4 h PLN 0.00, 12 h / 24 h PLN 5.00 (crosses Saturday 06–15).
/// Biedronka Opole 1–90 min PLN 0.00, 91 min PLN 2.00, 2 h 2.00, 4 h 10.00, 24 h 90.00 (= defaultLengthOfStay 90 and
/// its tariff text "1,5 h free, once per 4 h"); 30 days quotes PLN 2,698.00 (thousands separator!), 31 days still
/// quotes, so maxDuration 43200 is minutes and the quote doesn't enforce it. Both Jelenia Góra ids (with / without a
/// search position) are real spaces with identical details and both quote. A made-up promo code is ignored
/// silently (promocode=null, same price). Presets: no answer within 20 s (again). Benefits: []. Quotes list a
/// "Convenience fee" line (PLN 0.00 here).
/// </summary>
public sealed class QuotesScenario : IScenario
{
    public string Name => "quotes";
    public string Description => "Price quotes: duration ladders, free window, maxDuration unit, promo code, presets, benefits. Read-only.";

    private const string Ostrow = "c9a52a81-3c6f-4ddc-9879-21a9ad0e4cc8";
    private const string BiedronkaOpole = "f6d7d36f-1fda-4610-bab9-75d92ed62e77";
    private const string JeleniaGoraWithPosition = "c78e0ce2-64af-4592-b01b-4372e341f687";
    private const string JeleniaGoraWithoutPosition = "9edcfda3-8e5e-4b27-b612-3e7b75a32049";

    public async Task RunAsync(IServiceProvider services, DumpRun run, CancellationToken ct)
    {
        var api = services.GetRequiredService<IApcoaFlowApi>();
        var vehicle = (await api.GetVehiclesAsync(ct)).FirstOrDefault()
                      ?? throw new InvalidOperationException("The account has no vehicle.");
        run.Log($"vehicle {vehicle.LicensePlate} ({vehicle.Id})");
        var now = DateTimeOffset.UtcNow;

        async Task Quote(string label, string spaceId, int? seconds, string? promocode = null, bool dated = true)
        {
            try
            {
                var q = await api.GetPriceAsync(new PriceQuery
                {
                    SpaceId = spaceId, LicensePlateId = vehicle.Id, Duration = seconds, Promocode = promocode,
                    StartDate = dated ? UtcMillisecondsConverter.ToWire(now) : null,
                }, ct);
                run.Log($"   {label}: {q.TotalPrice} (prepayment {q.PrepaymentMode}, promocode={q.Promocode ?? "null"}, " +
                        $"items [{string.Join("; ", q.PriceItems.Select(i => $"{i.Label} {i.Price}"))}], " +
                        $"details [{string.Join("; ", q.PriceDetailItems.Select(i => $"{i.Label} {i.Price}"))}])");
            }
            catch (ApiException ex)
            {
                run.Log($"   {label}: HTTP {(int)ex.StatusCode} {ApcoaFlowApiError.TryRead(ex)?.Code} {ApcoaFlowApiError.TryRead(ex)?.Message ?? ex.Content}");
            }
        }

        async Task Space(string spaceId)
        {
            var s = await api.GetSpaceAsync(spaceId, ct: ct);
            run.Log($"space '{s.Name}' code={s.TariffCode} maxDuration={s.MaxDuration} defaultStay={s.DefaultLengthOfStayInMinutes} min; " +
                    $"tariff: {string.Join(" | ", s.Prices.Select(p => $"{p.SectionName}: {string.Join(", ", p.Prices.Select(i => $"{i.Label} {i.Price}"))} — {p.Description}"))}");
        }

        await Space(Ostrow);
        foreach (var (label, seconds) in Ladder())
            await Quote($"Ostrów {label}", Ostrow, seconds);
        await Quote("Ostrów 15 min, no StartDate", Ostrow, 900, dated: false);

        await Space(BiedronkaOpole);
        foreach (var (label, seconds) in Ladder().Concat([("90 min", 5400), ("91 min", 5460), ("13 h", 46800), ("30 days", 2_592_000), ("31 days", 2_678_400)]))
            await Quote($"Biedronka Opole {label}", BiedronkaOpole, seconds);

        foreach (var id in new[] { JeleniaGoraWithPosition, JeleniaGoraWithoutPosition })
        {
            try
            {
                await Space(id);
                await Quote($"Jelenia Góra id {id[..8]} 15 min", id, 900);
            }
            catch (ApiException ex)
            {
                run.Log($"Jelenia Góra id {id[..8]}: HTTP {(int)ex.StatusCode} {ApcoaFlowApiError.TryRead(ex)?.Code}");
            }
        }

        await Quote("Ostrów 15 min with promo code TESTCODE123", Ostrow, 900, promocode: "TESTCODE123");
        await Quote("Biedronka Opole 3 h with promo code TESTCODE123", BiedronkaOpole, 10800, promocode: "TESTCODE123");

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var presets = await api.GetLengthOfStayPresetsAsync(BiedronkaOpole, vehicle.Id, UtcMillisecondsConverter.ToWire(now), timeout.Token);
                run.Log($"presets (Biedronka Opole): {presets.GetRawText()[..Math.Min(1500, presets.GetRawText().Length)]}");
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException)
            {
                run.Log("presets (Biedronka Opole): no answer within 20 s");
            }
            catch (ApiException ex)
            {
                run.Log($"presets (Biedronka Opole): HTTP {(int)ex.StatusCode} {ex.Content}");
            }
        }

        var benefits = await api.GetBenefitsAsync(ct);
        run.Log($"benefits ({benefits.Count}): {string.Join(" ", benefits.Select(b => b.GetRawText()))}");
    }

    private static IEnumerable<(string Label, int Seconds)> Ladder() =>
    [
        ("1 min", 60), ("15 min", 900), ("30 min", 1800), ("1 h", 3600), ("2 h", 7200), ("4 h", 14400), ("12 h", 43200), ("24 h", 86400),
    ];
}
