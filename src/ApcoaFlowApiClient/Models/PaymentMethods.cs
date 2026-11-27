namespace ApcoaFlowApiClient.Models;

/// <summary>
/// <c>GET /api/v1/paymentMethods[?spaceId=]</c> item. With <c>spaceId</c> the list is filtered to what that space
/// accepts. Seen 2026-11-27: <c>masterCard</c> / <c>applePay</c> (<c>flow: saved</c>), and a pseudo-method
/// <c>{id: "hosted-checkout", type: "unknown", flow: "hostedCheckout"}</c> (an interactive Stripe page).
/// </summary>
public sealed class PaymentMethod
{
    public string Id { get; set; } = string.Empty;

    /// <summary><c>masterCard</c>, <c>visa</c>, <c>applePay</c>, <c>unknown</c>…</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Masked number, e.g. <c>****7894</c>.</summary>
    public string? Identifier { get; set; }

    public string? Expiration { get; set; }
    public string? Status { get; set; }
    public bool IsDefault { get; set; }
    public string? Psp { get; set; }

    /// <summary><c>saved</c> (charged without interaction) or <c>hostedCheckout</c>.</summary>
    public string? Flow { get; set; }

    /// <summary><c>none</c> when nothing is needed from the user.</summary>
    public string? RequiredAction { get; set; }

    /// <summary><c>available</c> when usable.</summary>
    public string? Availability { get; set; }
}
