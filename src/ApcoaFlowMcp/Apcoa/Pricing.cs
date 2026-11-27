using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Http;
using ApcoaFlowApiClient.Models;

namespace ApcoaFlowMcp.Apcoa;

/// <summary>
/// Quotes for a vehicle at a parking. A quote already includes whatever the parking gives this vehicle (free time such
/// as Biedronka's 1.5 h once per 4 h), so "free" always means "the dated quote is 0.00".
/// </summary>
public sealed class Pricing(IApcoaFlowApi api, TimeProvider time)
{
    /// <summary>The longest stay checked for a free window: one day.</summary>
    public const int FreeWindowCapMinutes = 24 * 60;

    /// <summary>A dated quote for <paramref name="minutes"/> starting now (what the app sends).</summary>
    public Task<PriceQuote> QuoteAsync(string spaceId, string vehicleId, int minutes, CancellationToken ct) =>
        api.GetPriceAsync(new PriceQuery
        {
            SpaceId = spaceId, LicensePlateId = vehicleId, Duration = minutes * 60,
            StartDate = UtcMillisecondsConverter.ToWire(time.GetUtcNow()),
        }, ct);

    /// <summary>How long a free stay can be.</summary>
    /// <param name="Minutes">The longest free stay, in minutes (0 = not free even for a minute).</param>
    /// <param name="AtLeast">True when it's free for the whole <see cref="FreeWindowCapMinutes"/> checked.</param>
    public readonly record struct FreeWindow(int Minutes, bool AtLeast);

    /// <summary>
    /// The longest stay starting now that quotes 0.00. Tries the parking's preselected duration first (Biedronka:
    /// 90 min = its free window; 2 quotes), else a binary search over up to a day (~12 quotes).
    /// </summary>
    public async Task<FreeWindow> FreeWindowAsync(string spaceId, string vehicleId, int? hintMinutes, int? maxMinutes, CancellationToken ct)
    {
        async Task<bool> Free(int minutes) => (await QuoteAsync(spaceId, vehicleId, minutes, ct)).Total.IsZero;

        if (!await Free(1))
            return new FreeWindow(0, AtLeast: false);

        var cap = Math.Min(FreeWindowCapMinutes, maxMinutes is > 0 ? maxMinutes.Value : FreeWindowCapMinutes);
        if (hintMinutes is > 1 && hintMinutes < cap && await Free(hintMinutes.Value) && !await Free(hintMinutes.Value + 1))
            return new FreeWindow(hintMinutes.Value, AtLeast: false);
        if (await Free(cap))
            return new FreeWindow(cap, AtLeast: cap == FreeWindowCapMinutes);

        int lo = 1, hi = cap; // free at lo, not free at hi
        while (hi - lo > 1)
        {
            var mid = lo + (hi - lo) / 2;
            if (await Free(mid)) lo = mid;
            else hi = mid;
        }
        return new FreeWindow(lo, AtLeast: false);
    }
}
