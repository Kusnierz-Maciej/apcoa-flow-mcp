using System.Text.Json;
using ApcoaFlow.Sandbox.Dumping;
using Microsoft.Extensions.DependencyInjection;

namespace ApcoaFlow.Sandbox.Scenarios;

/// <summary>
/// WHAT: what a parking's details say about its pricing, without quotes: <c>spaces/{id}</c> plain and with the app's
/// <c>priceOptions</c> query parameter (none, 1, 3, and 1–24 repeated), for a shop
/// parking with free time, the free test parking and a per-started-hour parking; diffs the answers. Read-only.
/// WHY: durations (free window, longest stay at the same price) should be deduced from the details, not searched
/// with quotes (user, 2026-11-28). The app's scan-and-pay flow reads <c>durationQuotes</c> next to the details.
/// FOUND (2026-11-28): <c>priceOptions</c> is a list of whole hours (repeat the parameter); the table then lists "Up to
/// N hours" = the price of a stay of up to N hours starting NOW, e.g. Przy Ratuszu +0.40/h until 8:00 then +1.30/h,
/// Ostrów free 1–5 h then 3.00 (Saturday 06:00), Biedronka 1 h 0.00 / 2 h 2.00. Not plate-specific (shop free time
/// used up for a plate isn't shown). "true"/"all"/words, "1,2,3" and "1.5" → 400; 0 → empty table. The plain table's
/// "Max day price" is the general day cap, not dated (Ostrów 0.00 while a dated day costs 5.00).
/// FOUND (2026-11-28, app code): the details screen sends no priceOptions (the API then answers hours 1, 2, 24); only
/// scan &amp; pay sends 1, 2, 3, 4. Rows are {label, price} written by the server; the app has no tariff model.
/// FOUND (2026-11-28): rows come back in the asked order ([3,1,2] → 3 h, 1 h, 2 h), duplicates merged ([5,1,5] → 5 h,
/// 1 h), hours the tariff doesn't sell left out (Chorzów 720 → a row, 721 → none; Biedronka, same maxDuration, answers
/// 1000 h — so it's the tariff, not maxDuration). Every hour except 24 = the dated quote
/// for that stay (Chorzów 23/25/30/48 h = 30/34/54/60, the quote for 24 h 1 min = 34). Hour 24 = "Max day price" =
/// the CURRENT DAY's maximum, not a dated 24 h stay: Ostrów Saturday 19:07 → 5.00 while 23 h, 25 h and the 1440-min
/// quote are 0.00; Saturday 00:08 local (Friday 23:08 UTC) → 0.00, so the day is probably the UTC day.
/// RULE (user, 2026-11-28): probe within 24 hours only (priceOptions 1–24); never ask for hundreds of hours again.
/// FOUND (2026-11-28): prices/lengthOfStayPresets still hangs; prices/preBooking with a quote's query → 400
/// ValidationError; a quote's startDate/endDate are null and its priceItems are display rows (Cost, Convenience fee).
/// FOUND (2026-11-28, web app in Chrome, recon docs/captures/0011): details without priceOptions; the duration picker
/// (+30 min / +1 h / +5 h / +1 d / end date) sends one dated quote per change (prices + StartDate), preselects
/// defaultLengthOfStayInMinutes (Biedronka 90 → 0.00), never shows "free until" or "same price until".
/// </summary>
public sealed class ParkingPricingScenario : IScenario
{
    public string Name => "parking-pricing";
    public string Description => "Pricing in parking details: spaces/{id} with priceOptions (hours) variants, diffed. Read-only.";

    private static readonly (string Name, string Id)[] Parkings =
    [
        ("Biedronka Opole (shop free time)", "f6d7d36f-1fda-4610-bab9-75d92ed62e77"),
        ("Ostrów (free test parking)", "c9a52a81-3c6f-4ddc-9879-21a9ad0e4cc8"),
        ("Przy Ratuszu (PLN 0.40 per started hour)", "986490f8-ccf5-4999-9a59-44a2e289c473"),
    ];

    private static readonly int[]?[] Options = [null, [1], [3], [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24]];

    public async Task RunAsync(IServiceProvider services, DumpRun run, CancellationToken ct)
    {
        var api = services.GetRequiredService<ApcoaFlowApiClient.Api.IApcoaFlowApi>();
        foreach (var (name, id) in Parkings)
        {
            string? baseline = null;
            foreach (var option in Options)
            {
                try
                {
                    var details = await api.GetSpaceAsync(id, option, ct);
                    var summary = Summarize(JsonSerializer.Serialize(details, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    baseline ??= summary;
                    run.Log($"{name} priceOptions={(option is null ? "(none)" : string.Join(",", option))}: " + (summary == baseline && option is not null ? "same as plain" : summary));
                }
                catch (Refit.ApiException ex)
                {
                    run.Log($"{name} priceOptions={(option is null ? "(none)" : string.Join(",", option))}: HTTP {(int)ex.StatusCode} {ex.Content?[..Math.Min(200, ex.Content.Length)]}");
                }
            }
        }
    }

    // Keys plus the pricing parts, so differences between variants stand out.
    private static string Summarize(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return json[..Math.Min(300, json.Length)];
            var keys = string.Join(",", root.EnumerateObject().Select(p => p.Name));
            string Part(string k) => root.TryGetProperty(k, out var v) ? v.GetRawText() : "-";
            return $"keys=[{keys}] prices={Part("prices")} defaultStay={Part("defaultLengthOfStayInMinutes")} max={Part("maxDuration")}";
        }
        catch (JsonException)
        {
            return json[..Math.Min(300, json.Length)];
        }
    }
}
