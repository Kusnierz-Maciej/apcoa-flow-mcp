using System.Text.Json.Serialization;
using ApcoaFlowApiClient.Http;

namespace ApcoaFlowApiClient.Models;

/// <summary>
/// <c>GET /api/v1/sessions</c> item: a running (or planned) session. There is no <c>GET sessions/{id}</c> (404); use
/// this list or <c>sessionsHistory/{id}</c>.
/// </summary>
public sealed class Session
{
    public string Id { get; set; } = string.Empty;
    public List<string>? ExtendedFromIds { get; set; }

    /// <summary><c>Parking</c> (or charging).</summary>
    public string? Type { get; set; }

    /// <summary><c>active</c> (others not seen yet).</summary>
    public string? Status { get; set; }

    public DateTimeOffset StartDate { get; set; }
    public DateTimeOffset? EndDate { get; set; }
    public bool SessionCanBeStopped { get; set; }
    public bool SessionCanBeExtended { get; set; }
    public SessionSpace? Space { get; set; }
    public SessionVehicle? Vehicle { get; set; }

    /// <summary><c>flexible</c>.</summary>
    public string? ExtensionType { get; set; }

    public string? PromoCode { get; set; }
    public bool CanApplyPromoCode { get; set; }

    /// <summary><c>prePay</c> (charged at start) or <c>postPay</c> (charged at stop).</summary>
    public string? PaymentFlow { get; set; }

    /// <summary>Summary lines, e.g. <c>{title: "Total", subTitle: "0", footer: "PLN"}</c>.</summary>
    public List<SessionDetail> Details { get; set; } = [];
}

public sealed class SessionSpace
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    [JsonConverter(typeof(StringOrNumberConverter))]
    public string? TariffCode { get; set; }

    public string? CountryCode { get; set; }
}

public sealed class SessionVehicle
{
    public string Id { get; set; } = string.Empty;
    public string LicensePlate { get; set; } = string.Empty;
    public string? CountryCode { get; set; }
    public string? Description { get; set; }
}

public sealed class SessionDetail
{
    public string? Title { get; set; }
    public string? SubTitle { get; set; }
    public string? Footer { get; set; }
}

/// <summary>
/// <c>POST /api/v1/sessions/startParking</c> body, as the web app sends it (header <c>idempotency-key: &lt;uuid&gt;</c>).
/// <c>paymentMethodId</c> is required in practice, even for a free start (the app sends the default method's id).
/// Dates are UTC with milliseconds (<c>2026-11-27T14:33:00.000Z</c>).
/// </summary>
public sealed class StartParkingRequest
{
    public string SpaceId { get; set; } = string.Empty;
    public string LicensePlateId { get; set; } = string.Empty;

    [JsonConverter(typeof(UtcMillisecondsConverter))]
    public DateTimeOffset StartDate { get; set; }

    [JsonConverter(typeof(UtcMillisecondsConverter))]
    public DateTimeOffset EndDate { get; set; }

    public string PaymentMethodId { get; set; } = string.Empty;
    public string? PaymentChoice { get; set; }
    public string? ExtendedSessionId { get; set; }
    public string? PromoCode { get; set; }
    public string? PriceRequestIdentifier { get; set; }
    public string? TariffPeriodId { get; set; }
    public string? CompanyBillingAddressId { get; set; }
    public bool IsPreBooked { get; set; }
}

/// <summary>
/// <c>startParking</c> answer. Two shapes seen (2026-11-27):
/// <list type="bullet">
/// <item>free starts (and a post-pay saved-card start): <c>{id}</c>, everything else null — running at once;</item>
/// <item>a paid <b>pre-pay</b> start with a saved card: <c>{id: null, pendingId}</c> — the start is processed
/// asynchronously; poll <c>GET sessions/pending/{pendingId}</c> until <c>status: "completed"</c> with the session id.</item>
/// </list>
/// A <see cref="ClientSecret"/> would mean the card payment needs an interactive step (Stripe, e.g. 3-D Secure), which
/// the MCP can't do.
/// </summary>
public sealed class StartParkingResponse
{
    public string? Id { get; set; }
    public string? Status { get; set; }
    public string? PendingId { get; set; }
    public string? ClientSecret { get; set; }
    public string? PrepaymentMode { get; set; }
    public string? PublishableKey { get; set; }

    /// <summary>The payment needs an interactive step (Stripe client secret).</summary>
    public bool NeedsPaymentAction => !string.IsNullOrEmpty(ClientSecret);

    /// <summary>The start is processed asynchronously: poll <see cref="PendingId"/>.</summary>
    public bool IsPending => string.IsNullOrEmpty(Id) && !string.IsNullOrEmpty(PendingId);
}

/// <summary>
/// <c>GET /api/v1/sessions/pending/{pendingId}</c>: <c>{status, sessionId, failureReason}</c>. Seen: <c>status:
/// "completed"</c> with the new session id (2026-11-27). Other values (in progress, failed) not seen yet.
/// </summary>
public sealed class PendingStartStatus
{
    public string? Status { get; set; }
    public string? SessionId { get; set; }
    public string? FailureReason { get; set; }

    public bool IsCompleted => string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(SessionId);

    /// <summary>Settled without a session: a failure reason, or a status that says so.</summary>
    public bool IsFailed => !string.IsNullOrEmpty(FailureReason) ||
                            Status is { } s && (s.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                                                s.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
                                                s.Contains("reject", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// <c>updateParkingDuration</c> answer when it has a body. A free extension's body was empty; a paid pre-pay one
/// answered <c>{"status":"awaitingPayment","pendingId":…,"failureReason":null}</c> and went through within seconds
/// (2026-11-27): processed asynchronously, so confirm by the session's new end.
/// </summary>
public sealed class UpdateParkingDurationResponse
{
    public string? Status { get; set; }
    public string? SessionId { get; set; }
    public string? PendingId { get; set; }
    public string? FailureReason { get; set; }
    public string? ClientSecret { get; set; }
}

/// <summary><c>POST /api/v1/sessions/updateParkingDuration</c> body; the same session continues with the new end.</summary>
public sealed class UpdateParkingDurationRequest
{
    public string SessionId { get; set; } = string.Empty;

    [JsonConverter(typeof(UtcMillisecondsConverter))]
    public DateTimeOffset NewEnd { get; set; }

    public string PaymentMethodId { get; set; } = string.Empty;
    public string? PromoCode { get; set; }
}
