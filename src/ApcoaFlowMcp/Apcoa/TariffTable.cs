using ApcoaFlowApiClient.Models;

namespace ApcoaFlowMcp.Apcoa;

/// <summary>
/// A parking's hourly prices, read from its details asked with <c>priceOptions</c> = <see cref="CurveHours"/>
/// (2026-11-28, sandbox <c>parking-pricing</c>):
/// <list type="bullet">
/// <item>The API answers one row per hour asked, <b>in the asked order</b>; duplicates are merged (we ask distinct hours)
/// and hours the tariff doesn't sell (the longest) are left out. So a row's hour comes from its <b>position</b>. Row
/// labels ("Up to 2 hours", "Max day price") are display text and are never read.</item>
/// <item>Every hour except 24 is the dated price of a stay of up to that many hours starting now (= the quote).</item>
/// <item>Hour 24 is the "Max day price": the current day's maximum, not a dated 24 h stay (Ostrów on a Saturday evening:
/// 5.00 while a 24 h stay quotes 0.00). It is kept apart as <see cref="Curve.DayMax"/>.</item>
/// </list>
/// The rows are the same for every vehicle, so a deduction from them gets one verifying quote.
/// </summary>
public static class TariffTable
{
    /// <summary>The hours asked for as <c>priceOptions</c>: 1–23 for the curve, 24 for the day maximum.</summary>
    public static readonly int[] CurveHours = Enumerable.Range(1, 24).ToArray();

    private const int DayMaxHour = 24;

    /// <summary>A price mark: a stay of up to <see cref="Minutes"/> starting now costs <see cref="Price"/>.</summary>
    public readonly record struct Mark(int Minutes, Money Price);

    /// <summary>A row as the server writes it, label and price untouched (e.g. "Up to 2 hours", "PLN 7.00").</summary>
    public readonly record struct Row(string Label, string Price);

    /// <summary>The hours whose rows the APCOA Flow app shows on the details screen: 1, 2 and 24 ("Max day price").</summary>
    private static readonly int[] ShownHours = [1, 2, DayMaxHour];

    /// <summary>
    /// The dated marks (hours 1–23, shortest first), the current day's maximum when known, and the rows the app shows
    /// (hours 1, 2, 24) exactly as the server wrote them — to show to the user as-is, never to compute from.
    /// </summary>
    public sealed record Curve(List<Mark> Marks, Money? DayMax, List<Row> Shown)
    {
        public static readonly Curve None = new([], null, []);

        /// <summary>The price of a stay of up to <paramref name="hours"/> hours starting now, when on the curve.</summary>
        public Money? At(int hours) => Marks.FirstOrDefault(m => m.Minutes == hours * 60) is { Minutes: > 0 } m ? m.Price : null;
    }

    /// <summary>
    /// The curve of <paramref name="parking"/>, fetched with <c>priceOptions</c> = <see cref="CurveHours"/> (ascending).
    /// The section is found by shape (every row a price). The API leaves out hours its tariff doesn't sell, and those are
    /// always the longest ones (Chorzów: none beyond 720 h), so the n rows are the first n hours asked. More rows than
    /// hours asked, or no such section, gives <see cref="Curve.None"/>, and callers fall back to quotes.
    /// </summary>
    public static Curve Read(SpaceDetails parking)
    {
        foreach (var section in parking.Prices)
        {
            var prices = new List<Money>();
            foreach (var item in section.Prices)
            {
                if (!Money.TryParse(item.Price, out var price)) break;
                prices.Add(price);
            }
            if (prices.Count == 0 || prices.Count != section.Prices.Count || prices.Count > CurveHours.Length)
                continue;

            var marks = new List<Mark>();
            var shown = new List<Row>();
            Money? dayMax = null;
            for (var i = 0; i < prices.Count; i++)
            {
                if (CurveHours[i] == DayMaxHour) dayMax = prices[i];
                else marks.Add(new Mark(CurveHours[i] * 60, prices[i]));
                if (ShownHours.Contains(CurveHours[i]))
                    shown.Add(new Row(section.Prices[i].Label?.Trim() ?? "", section.Prices[i].Price!.Trim()));
            }
            return new Curve(marks, dayMax, shown);
        }
        return Curve.None;
    }

    /// <summary>
    /// The longest mark at or beyond <paramref name="minutes"/> that costs <paramref name="price"/>, with every mark in
    /// between at that price too; null if the first mark at or beyond it already costs something else.
    /// </summary>
    public static int? LongestAtPrice(List<Mark> marks, int minutes, Money price)
    {
        int? longest = null;
        foreach (var mark in marks.Where(m => m.Minutes >= minutes))
        {
            if (mark.Price.Amount != price.Amount) break;
            longest = mark.Minutes;
        }
        return longest;
    }

    /// <summary>The free part of the curve: the last mark of the 0.00 run from the start, and the first paid mark after it.</summary>
    public static (int? LastFree, int? FirstPaid) FreeRun(List<Mark> marks)
    {
        int? lastFree = null;
        foreach (var mark in marks)
        {
            if (!mark.Price.IsZero) return (lastFree, mark.Minutes);
            lastFree = mark.Minutes;
        }
        return (lastFree, null);
    }

    /// <summary>
    /// The best guess for how long a stay starting now is free, from the curve and the parking's preselected duration
    /// (<c>defaultLengthOfStayInMinutes</c>: shops preselect their free window, e.g. Biedronka 90 min). When the
    /// preselected duration falls inside the last free hour, it's the exact window. Otherwise the last free hour is a
    /// lower bound. Without a curve the preselected duration is the guess. Null when nothing suggests free time. Not
    /// vehicle-specific: verify with a quote.
    /// </summary>
    public static (int Minutes, bool Exact)? FreeGuess(Curve curve, int? preselectedMinutes)
    {
        var (lastFree, firstPaid) = FreeRun(curve.Marks);
        if (preselectedMinutes is > 0 and var hint && hint > (lastFree ?? 0) && (firstPaid is null || hint < firstPaid))
            return (hint, firstPaid is not null); // no curve at all: the preselected stay is still the best guess
        return lastFree is { } free ? (free, false) : null;
    }
}
