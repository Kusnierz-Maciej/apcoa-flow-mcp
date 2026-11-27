using System.Globalization;
using System.Text.RegularExpressions;
using ApcoaFlowApiClient.Models;

namespace ApcoaFlowMcp.Apcoa;

/// <summary>
/// The price marks in a parking's tariff table (<c>spaces/{id}</c> → <c>prices</c>, section "Parking"): "Up to N hours"
/// rows, the price of a stay of up to N hours starting now. With <see cref="CurveHours"/> as <c>priceOptions</c> the
/// table has one row per hour — the price curve of the next day (2026-11-28). "Max day price" (the plain table's last
/// row) is the general day cap, not dated, so it's left out. Not plate-specific: verify a deduction with one quote.
/// </summary>
public static partial class TariffTable
{
    /// <summary>The hours asked for as <c>priceOptions</c>: the next day, hour by hour.</summary>
    public static readonly int[] CurveHours = Enumerable.Range(1, 24).ToArray();

    /// <summary>A price mark: a stay of up to <see cref="Minutes"/> starting now costs <see cref="Price"/>.</summary>
    public readonly record struct Mark(int Minutes, Money Price);

    [GeneratedRegex(@"^up to (?<n>\d+)\s*(?<unit>minutes?|mins?|hours?|h|days?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UpTo();

    /// <summary>The dated marks, shortest first; other rows ("Max day price", notes without a price) are skipped.</summary>
    public static List<Mark> Marks(SpaceDetails parking)
    {
        var marks = new List<Mark>();
        foreach (var item in parking.Prices.Where(s => s.SectionName is null || s.SectionName.Equals("Parking", StringComparison.OrdinalIgnoreCase))
                                           .SelectMany(s => s.Prices))
        {
            if (!Money.TryParse(item.Price, out var price) || UpTo().Match(item.Label?.Trim() ?? "") is not { Success: true } m)
                continue;
            var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            var unit = m.Groups["unit"].Value.ToLowerInvariant();
            marks.Add(new Mark(unit.StartsWith("min") ? n : unit.StartsWith('d') ? n * 24 * 60 : n * 60, price));
        }
        return marks.OrderBy(m => m.Minutes).DistinctBy(m => m.Minutes).ToList();
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
}
