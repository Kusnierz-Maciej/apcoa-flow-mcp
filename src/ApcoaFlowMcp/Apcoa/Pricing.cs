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
    /// A parking's details WITH its price curve (the next day, hour by hour, starting now): one call that serves as the
    /// details too (same fields). Pass the result to <see cref="FreeWindowAsync"/> and <see cref="LongestAtSamePriceAsync"/>.
    /// </summary>
    public async Task<SpaceDetails> DetailsWithCurveAsync(string parkingId, CancellationToken ct)
    {
        return await api.GetParkingAsync(parkingId, TariffTable.CurveHours, ct);
    }

    /// <summary>
    /// The longest stay starting now that costs the same as <paramref name="minutes"/> did (<paramref name="price"/>),
    /// read off the parking's hourly price curve (<paramref name="parking"/> from <see cref="DetailsWithCurveAsync"/>) and verified with ONE quote (the curve isn't plate-specific); null when
    /// the curve shows no longer stay at that price, or the quote disagrees. No searching.
    /// </summary>
    public async Task<int?> LongestAtSamePriceAsync(SpaceDetails parking, string vehicleId, int minutes, Money price, CancellationToken ct)
    {
        if (TariffTable.LongestAtPrice(TariffTable.Read(parking).Marks, minutes, price) is not { } candidate) return null;
        if (parking.MaxDuration is > 0 && candidate > parking.MaxDuration) candidate = parking.MaxDuration.Value;
        if (candidate <= minutes) return null;
        return (await QuoteAsync(parking.Id, vehicleId, candidate, ct)).Total.Amount == price.Amount ? candidate : null;
    }

    /// <summary>
    /// How long a stay starting now is free, read off the parking's own data and verified with one quote — no searching:
    /// the hourly price curve gives the free run (e.g. free up to 5 h, paid by 6 h); the parking's preselected duration
    /// (shops: their free window, e.g. Biedronka 90 min) pins it down when it falls inside that last hour. The quote
    /// checks it for this vehicle (a shop's free time can be used up for a plate). Within the last hour only the lower
    /// bound is known, unless the preselected duration says so.
    /// </summary>
    public async Task<FreeWindow> FreeWindowAsync(SpaceDetails parking, string vehicleId, CancellationToken ct)
    {
        async Task<bool> Free(int minutes) => (await QuoteAsync(parking.Id, vehicleId, minutes, ct)).Total.IsZero;
        int Capped(int m) => parking.MaxDuration is > 0 && m > parking.MaxDuration ? parking.MaxDuration.Value : m;

        var guess = TariffTable.FreeGuess(TariffTable.Read(parking), parking.DefaultLengthOfStayInMinutes);
        int? candidate = guess?.Minutes;
        var exact = guess?.Exact ?? false;

        if (candidate is { } c && await Free(Capped(c)))
            return new FreeWindow(Capped(c), AtLeast: !exact);

        // The curve says paid (or the free time is used up for this vehicle): free right now at all?
        return await Free(1) ? new FreeWindow(1, AtLeast: true) : new FreeWindow(0, AtLeast: false);
    }
}
