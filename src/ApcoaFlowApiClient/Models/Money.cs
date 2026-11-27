using System.Globalization;

namespace ApcoaFlowApiClient.Models;

/// <summary>
/// An amount as the API formats it: <c>"PLN 5.00"</c> (currency, space, invariant decimal; large amounts with a
/// thousands separator, <c>"PLN 2,698.00"</c>). Every price field
/// (<c>totalPrice</c>, <c>priceItems[].price</c>, history <c>totalPrice</c>) uses this form.
/// </summary>
public readonly record struct Money(string Currency, decimal Amount)
{
    public static bool TryParse(string? text, out Money money)
    {
        money = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            return false;
        money = new Money(parts[0], amount);
        return true;
    }

    /// <exception cref="FormatException">The text isn't "CUR 0.00".</exception>
    public static Money Parse(string? text) =>
        TryParse(text, out var money) ? money : throw new FormatException($"Unexpected price format: '{text}'.");

    public bool IsZero => Amount == 0m;

    public override string ToString() => $"{Amount.ToString("0.00", CultureInfo.InvariantCulture)} {Currency}";
}
