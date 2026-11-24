using System.Text.Json.Serialization;
using ApcoaFlowApiClient.Converters;

namespace ApcoaFlowApiClient.Models.Promotion;

/// <summary>
/// A promotion/coupon from <c>POST /promotion/applicableOffersWithPromotionV2</c>. Free-parking offers
/// have <see cref="ConcessionType"/> == "free_time" and carry the free window in <see cref="ConcessionValue"/>
/// (minutes, serialized as a string). The free window comes from this metadata, never from <see cref="Name"/>.
/// </summary>
public class Offer
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? ShortDescription { get; set; }
    public string? LongDescription { get; set; }

    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? OfferStartsAt { get; set; }
    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? OfferExpiresAt { get; set; }

    /// <summary>1 when the offer is currently active.</summary>
    public int IsActive { get; set; }
    public int IsDisplay { get; set; }

    public string? OfferDiscountType { get; set; }
    public int AutoApply { get; set; }

    /// <summary>e.g. "free_time" for free-duration coupons.</summary>
    public string? ConcessionType { get; set; }

    /// <summary>For "free_time": the free window (API sends it as a string number of minutes, e.g. "120").</summary>
    [JsonConverter(typeof(StringMinutesTimeSpanConverter))]
    public TimeSpan? ConcessionValue { get; set; }

    public string? Company { get; set; }
    public string? Tenant { get; set; }

    /// <summary>True when this is an active free-time coupon.</summary>
    [JsonIgnore]
    public bool IsActiveFreeTime =>
        IsActive == 1 && string.Equals(ConcessionType, "free_time", StringComparison.OrdinalIgnoreCase);

    /// <summary>The free window (from <see cref="ConcessionValue"/>), or null if not an active free-time offer.</summary>
    [JsonIgnore]
    public TimeSpan? FreeWindow => IsActiveFreeTime ? ConcessionValue : null;

    /// <summary>Convenience: the free window in whole minutes, or null.</summary>
    [JsonIgnore]
    public int? FreeMinutes => FreeWindow is { } w ? (int)w.TotalMinutes : null;
}
