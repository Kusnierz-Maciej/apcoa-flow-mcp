using System.Text.Json.Serialization;
using ApcoaFlowApiClient.Http;

namespace ApcoaFlowApiClient.Models;

/// <summary><c>GET /api/v1/sessionsHistory?page=1&amp;size=8</c> item (newest first).</summary>
public sealed class SessionsHistoryItem
{
    public string Id { get; set; } = string.Empty;
    public string? SpaceName { get; set; }

    /// <summary><c>parking</c>.</summary>
    public string? Type { get; set; }

    public DateTimeOffset? EndDate { get; set; }
    public string? TotalPrice { get; set; }

    /// <summary><c>paid</c> (others not seen yet).</summary>
    public string? PaymentStatus { get; set; }

    public string? LicensePlate { get; set; }

    /// <summary><c>billed</c> once done.</summary>
    public string? State { get; set; }

    public string? SubType { get; set; }
}

/// <summary>
/// <c>GET /api/v1/sessionsHistory/{id}</c>. Works for an active prePay session too (<c>isSessionActive: true</c>), but
/// an active postPay one is <c>404 SessionNotFound</c>. <see cref="EndDate"/>: the real stop time for postPay; the
/// planned end for prePay, even after an early stop.
/// </summary>
public sealed class SessionsHistoryDetails
{
    public string Id { get; set; } = string.Empty;
    public string? Type { get; set; }
    public string? SpaceName { get; set; }
    public string? SpaceId { get; set; }

    [JsonConverter(typeof(StringOrNumberConverter))]
    public string? TariffCode { get; set; }

    public List<string> ReceiptUrls { get; set; } = [];
    public HistoryPaymentMethod? PaymentMethod { get; set; }
    public DateTimeOffset StartDate { get; set; }
    public DateTimeOffset? EndDate { get; set; }

    /// <summary><c>paid</c>.</summary>
    public string? Status { get; set; }

    public string? LicensePlate { get; set; }
    public List<PriceItem> PriceItems { get; set; } = [];
    public string? TotalPrice { get; set; }
    public string? PromoCode { get; set; }
    public bool IsSessionActive { get; set; }

    /// <summary><c>active</c> or <c>billed</c>.</summary>
    public string? State { get; set; }

    public bool UserOwesNothing { get; set; }
}

public sealed class HistoryPaymentMethod
{
    public string? PaymentId { get; set; }
    public string? Type { get; set; }
    public string? Identifier { get; set; }
}
