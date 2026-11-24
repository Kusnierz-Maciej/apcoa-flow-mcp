using System.Text.Json.Serialization;
using ApcoaFlowApiClient.Converters;

namespace ApcoaFlowApiClient.Models.Session;

/// <summary>
/// A parking session as returned by <c>GET /consumer/parkingSessionV2</c>. Carries the parking name
/// and address inline (no extra lookup needed), the plate, and the total <see cref="Amount"/>.
/// </summary>
public class ParkingSessionV2
{
    public long Id { get; set; }
    public int ParkingId { get; set; }
    public int ParkingLotId { get; set; }
    public string? ParkingName { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? CountryCode { get; set; }

    /// <summary>ACTIVE | COMPLETED | IN_REVIEW …</summary>
    public string ParkingSessionStatus { get; set; } = string.Empty;

    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? CheckinTime { get; set; }
    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? CheckoutTime { get; set; }
    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? ScheduledCheckinTime { get; set; }
    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? ScheduledCheckoutTime { get; set; }

    public string? CurrencyCode { get; set; }

    /// <summary>Total charged for the session (0 for free sessions).</summary>
    public decimal Amount { get; set; }

    /// <summary>License plate used to enter.</summary>
    public string? EntryConsumerIdentificationExternalIdentifier { get; set; }

    public int ConsumerUserId { get; set; }

    public List<ParkingSessionDetail> ParkingSessionDetailList { get; set; } = [];

    /// <summary>Convenience: the plate, from the entry identifier.</summary>
    [JsonIgnore]
    public string? LicensePlate => EntryConsumerIdentificationExternalIdentifier;

    [JsonIgnore]
    public bool IsActive => string.Equals(ParkingSessionStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Actual parked duration (checkout − checkin), when both are known.</summary>
    [JsonIgnore]
    public TimeSpan? Duration =>
        CheckinTime is { } ci && CheckoutTime is { } co ? co - ci : null;
}

public class ParkingSessionDetail
{
    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? StartTime { get; set; }
    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? EndTime { get; set; }
    public decimal Amount { get; set; }
    public string? CardType { get; set; }
    public string? PaymentMethodType { get; set; }

    /// <summary>PARKING_SESSION | PARKING_SESSION_EXTENSION</summary>
    public string? ParkingSessionDetailType { get; set; }
    public string? TransactionStatus { get; set; }
}
