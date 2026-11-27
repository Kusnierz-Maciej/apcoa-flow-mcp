namespace ApcoaFlowApiClient.Models;

/// <summary>A label + formatted price line (<c>{"label": "Cost", "price": "PLN 0.00"}</c>).</summary>
public sealed class PriceItem
{
    public string? Label { get; set; }
    public string? Price { get; set; }
}

/// <summary>
/// A quote: <c>GET /api/v1/prices?SpaceId&amp;LicensePlateId&amp;Duration&amp;StartDate</c> (start) and
/// <c>GET /api/v1/prices/updatedPrice?sessionId&amp;duration</c> (extend). Durations are in <b>seconds</b>. The quote
/// already includes whatever the space gives this plate (Biedronka's free 1.5 h shows as PLN 0.00).
/// </summary>
public sealed class PriceQuote
{
    public List<PriceItem> PriceItems { get; set; } = [];
    public List<PriceItem> PriceDetailItems { get; set; } = [];

    /// <summary>What the user pays, e.g. <c>"PLN 5.00"</c>; see <see cref="Total"/>.</summary>
    public string? TotalPrice { get; set; }

    public string? Promocode { get; set; }
    public string? PriceRequestIdentifier { get; set; }
    public DateTimeOffset? StartDate { get; set; }

    /// <summary>Set by <c>updatedPrice</c>: the session's new end.</summary>
    public DateTimeOffset? EndDate { get; set; }

    /// <summary>True when the space charges at start (<see cref="PrepaymentMode"/> <c>autoCharge</c>).</summary>
    public bool RequiresPrepayment { get; set; }

    /// <summary><c>autoCharge</c> (prePay) or <c>none</c> (postPay: charged at stop).</summary>
    public string? PrepaymentMode { get; set; }

    public PrepaymentMethod? PrepaymentMethod { get; set; }
    public bool HostedCheckoutAllowed { get; set; }

    public Money Total => Money.Parse(TotalPrice);
}

public sealed class PrepaymentMethod
{
    public string? PaymentMethodId { get; set; }
    public string? MaskedIdentifier { get; set; }
    public string? Flow { get; set; }
}
