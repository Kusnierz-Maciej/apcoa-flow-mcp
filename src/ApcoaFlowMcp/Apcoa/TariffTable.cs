using System.Globalization;
using System.Text.RegularExpressions;
using ApcoaFlowApiClient.Models;

namespace ApcoaFlowMcp.Apcoa;

/// <summary>
/// The price marks in a parking's tariff table (<c>spaces/{id}</c> → <c>prices</c>, section "Parking"). Every table
/// sampled had the same rows (60 of 60, 2026-11-27): "Up to 1 hour", "Up to 2 hours", "Max day price" — i.e. what a stay
/// of up to 60, 120 and 1440 minutes costs. Used to deduce durations instead of searching with quotes; every deduction
/// is verified with one quote before it's offered.
/// </summary>
public static partial class TariffTable
{
    /// <summary>A price mark: a stay of up to <see cref="Minutes"/> costs <see cref="Price"/>.</summary>
    public readonly record struct Mark(int Minutes, Money Price);

    [GeneratedRegex(@"^up to (?<n>\d+)\s*(?<unit>minutes?|mins?|hours?|h|days?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UpTo();

    /// <summary>The marks, shortest first; rows that aren't "Up to N …" / "Max day price" or have no price are skipped.</summary>
    public static List<Mark> Marks(SpaceDetails parking)
    {
        var marks = new List<Mark>();
        foreach (var item in parking.Prices.Where(s => s.SectionName is null || s.SectionName.Equals("Parking", StringComparison.OrdinalIgnoreCase))
                                           .SelectMany(s => s.Prices))
        {
            if (!Money.TryParse(item.Price, out var price)) continue;
            var label = item.Label?.Trim() ?? "";
            int? minutes = null;
            if (label.Equals("Max day price", StringComparison.OrdinalIgnoreCase))
                minutes = 24 * 60;
            else if (UpTo().Match(label) is { Success: true } m)
            {
                var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
                var unit = m.Groups["unit"].Value.ToLowerInvariant();
                minutes = unit.StartsWith("min") ? n : unit.StartsWith('d') ? n * 24 * 60 : n * 60;
            }
            if (minutes is > 0)
                marks.Add(new Mark(minutes.Value, price));
        }
        return marks.OrderBy(m => m.Minutes).DistinctBy(m => m.Minutes).ToList();
    }

    /// <summary>
    /// The longest mark at or beyond <paramref name="minutes"/> that the table says costs <paramref name="price"/>, with every
    /// mark in between at that price too (prices only grow with time); null if none.
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
}
