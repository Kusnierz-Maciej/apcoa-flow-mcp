using System.Globalization;
using System.Text.RegularExpressions;

namespace ApcoaFlowApiClient.Models;

/// <summary>
/// An amount as the API formats it: <c>"PLN 5.00"</c> (currency, space, invariant decimal; large amounts with a
/// thousands separator, <c>"PLN 2,698.00"</c>). Every price field
/// (<c>totalPrice</c>, <c>priceItems[].price</c>, history <c>totalPrice</c>) uses this form.
/// </summary>
public readonly record struct Money(string Currency, decimal Amount)
{
    // "PLN 5.00", "PLN 2,698.00": a currency code, then digits with optional comma thousands groups and a dot decimal.
    // Strict on purpose: a lenient parse would read "PLN 5,00" as 500.
    private static readonly Regex Format = new(@"^(?<cur>[A-Z]{3}) (?<amount>-?\d{1,3}(?:,\d{3})*(?:\.\d+)?|-?\d+(?:\.\d+)?)$", RegexOptions.CultureInvariant);

    public static bool TryParse(string? text, out Money money)
    {
        money = default;
        var match = Format.Match(text?.Trim() ?? "");
        if (!match.Success ||
            !decimal.TryParse(match.Groups["amount"].Value.Replace(",", ""), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var amount))
            return false;
        money = new Money(match.Groups["cur"].Value, amount);
        return true;
    }

    /// <exception cref="FormatException">The text isn't "CUR 0.00" (mapped to a plain tool error).</exception>
    public static Money Parse(string? text) =>
        TryParse(text, out var money) ? money : throw new FormatException($"Unexpected price format: '{text}'.");

    public bool IsZero => Amount == 0m;

    public override string ToString() => $"{Amount.ToString("0.00", CultureInfo.InvariantCulture)} {Currency}";
}
