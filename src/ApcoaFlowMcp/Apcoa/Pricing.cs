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
    /// <summary>A dated quote for <paramref name="minutes"/> starting now (what the app sends).</summary>
    public Task<PriceQuote> QuoteAsync(string parkingId, string vehicleId, int minutes, CancellationToken ct) =>
        api.GetPriceAsync(new PriceQuery
        {
            SpaceId = parkingId, LicensePlateId = vehicleId, Duration = minutes * 60,
            StartDate = UtcMillisecondsConverter.ToWire(time.GetUtcNow()),
        }, ct);

    /// <summary>How long a free stay can be.</summary>
    /// <param name="Minutes">The longest free stay known, in minutes (0 = not free even for a minute).</param>
    /// <param name="AtLeast">True when it may be free for longer than <see cref="Minutes"/> (only a lower bound is known).</param>
    public readonly record struct FreeWindow(int Minutes, bool AtLeast);

    /// <summary>
    /// The longest stay starting now that costs the same as <paramref name="minutes"/> did (<paramref name="price"/>),
    /// deduced from the parking's tariff table and verified with ONE quote; null when the table doesn't show a longer stay
    /// at that price, or the quote disagrees. No searching.
    /// </summary>
    public async Task<int?> LongestAtSamePriceAsync(SpaceDetails parking, string vehicleId, int minutes, Money price, CancellationToken ct)
    {
        var longest = TariffTable.LongestAtPrice(TariffTable.Marks(parking), minutes, price);
        if (longest is not { } candidate || candidate <= minutes) return null;
        if (parking.MaxDuration is > 0 && candidate > parking.MaxDuration) candidate = parking.MaxDuration.Value;
        if (candidate <= minutes) return null;
        return (await QuoteAsync(parking.Id, vehicleId, candidate, ct)).Total.Amount == price.Amount ? candidate : null;
    }

    /// <summary>
    /// How long a stay starting now is free, from what the parking says about itself, verified with quotes — no searching:
    /// <list type="number">
    /// <item>1 min free? (1 quote) If not: not free.</item>
    /// <item>The parking's preselected duration (shops: their free window, e.g. Biedronka 90 min): free, and a minute more
    /// isn't? Then that's exactly the window (2 quotes).</item>
    /// <item>Else the longest mark the tariff table shows as 0.00 (1 h, 2 h, a day), verified with a quote: free for at
    /// least that long.</item>
    /// <item>Else: free for at least 1 minute (length unknown).</item>
    /// </list>
    /// </summary>
    public async Task<FreeWindow> FreeWindowAsync(SpaceDetails parking, string vehicleId, CancellationToken ct)
    {
        async Task<bool> Free(int minutes) => (await QuoteAsync(parking.Id, vehicleId, minutes, ct)).Total.IsZero;
        int? Capped(int m) => parking.MaxDuration is > 0 && m > parking.MaxDuration ? null : m;

        if (!await Free(1))
            return new FreeWindow(0, AtLeast: false);

        if (parking.DefaultLengthOfStayInMinutes is { } hint && hint > 1 && Capped(hint + 1) is not null
            && await Free(hint) && !await Free(hint + 1))
            return new FreeWindow(hint, AtLeast: false);

        foreach (var mark in TariffTable.Marks(parking).Where(m => m.Price.IsZero).OrderByDescending(m => m.Minutes))
            if (Capped(mark.Minutes) is { } m && m > 1 && await Free(m))
                return new FreeWindow(m, AtLeast: true);

        return new FreeWindow(1, AtLeast: true);
    }
}
