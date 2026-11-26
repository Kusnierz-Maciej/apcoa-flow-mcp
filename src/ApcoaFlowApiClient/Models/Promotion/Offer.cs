using System.Text.Json.Serialization;
using ApcoaFlowApiClient.Converters;

namespace ApcoaFlowApiClient.Models.Promotion;

/// <summary>
/// A promotion/coupon returned by the offers endpoints (<c>applicableOffersWithPromotion</c> /
/// <c>…V2</c>). Free-parking offers have <see cref="ConcessionType"/> == "free_time" and carry the free
/// window in <see cref="ConcessionValue"/> (minutes, serialized as a string). The free window comes from
/// this metadata, never from <see cref="Name"/>. The offer's own <see cref="Id"/>/<see cref="Name"/> are
/// a DISPLAY code; the promotion the API actually accepts is in <see cref="PromotionResponseList"/> —
/// use <see cref="PromotionId"/>/<see cref="PromotionName"/> for validate / breakdown / start.
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

    /// <summary>
    /// The promotions nested under this offer. The offer's top-level <see cref="Id"/>/<see cref="Name"/>
    /// are the OFFER code/display name (e.g. 4107 / "Darmowe60minutparkowania."); the promotion the API
    /// actually accepts for validate / tariff breakdown / start lives here (e.g. 104539 / "FRE60MIN07").
    /// Sending the offer name instead of the promotion name makes promotion/validate return 422
    /// INVALID_PROMOTION_CODE. Use <see cref="PromotionId"/>/<see cref="PromotionName"/>.
    /// </summary>
    public List<PromotionResponse> PromotionResponseList { get; set; } = [];

    /// <summary>The primary nested promotion (isPrimary==1), else the first, else null.</summary>
    [JsonIgnore]
    public PromotionResponse? PrimaryPromotion =>
        PromotionResponseList.FirstOrDefault(p => p.IsPrimary == 1) ?? PromotionResponseList.FirstOrDefault();

    /// <summary>The id the API expects for this promotion: the nested promotion's id, else the offer id.</summary>
    [JsonIgnore]
    public int PromotionId => PrimaryPromotion?.Id ?? Id;

    /// <summary>The name the API expects for this promotion: the nested promotion's name, else the offer name.</summary>
    [JsonIgnore]
    public string? PromotionName => PrimaryPromotion?.Name ?? Name;

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

    /// <summary>
    /// How long until this coupon can be used again after a session consumes it (the reuse cooldown),
    /// from the primary nested promotion's <see cref="PromotionResponse.ReuseCooldown"/>. Null when the
    /// offer has no nested promotion or the promotion carries no recognised cooldown.
    /// </summary>
    [JsonIgnore]
    public TimeSpan? ReuseCooldown => PrimaryPromotion?.ReuseCooldown;

    /// <summary>
    /// True when the primary promotion carries a cooldown amount whose unit was not recognised — i.e.
    /// <see cref="ReuseCooldown"/> is null because of an unmapped <c>promotionDurationUsageType</c>, not
    /// because there was no cooldown. Callers can log the raw unit (<see cref="ReuseCooldownUnit"/>).
    /// </summary>
    [JsonIgnore]
    public bool HasUnrecognisedCooldownUnit => PrimaryPromotion?.HasUnrecognisedCooldownUnit ?? false;

    /// <summary>The raw <c>promotionDurationUsageType</c> of the primary promotion (for logging unmapped units).</summary>
    [JsonIgnore]
    public string? ReuseCooldownUnit => PrimaryPromotion?.PromotionDurationUsageType;
}

/// <summary>
/// A promotion nested in an <see cref="Offer"/>'s <c>promotionResponseList</c>. This carries the id/name
/// that <c>promotion/validate</c>, <c>tariffBreakDownV2</c> and the start request actually accept —
/// distinct from the parent offer's code/display name.
/// </summary>
public class PromotionResponse
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public int OfferCode { get; set; }
    public string? OfferName { get; set; }

    /// <summary>1 for the primary promotion of its offer.</summary>
    public int IsPrimary { get; set; }
    public int IsActive { get; set; }

    public string? UsageType { get; set; }
    public int? UsageLimit { get; set; }

    /// <summary>
    /// The reuse-cooldown amount: how many <see cref="PromotionDurationUsageType"/> units must pass
    /// before the coupon can be used again (e.g. 240 with "PROMO_MINUTE" = once every 4 hours).
    /// </summary>
    public int? PromotionDurationUsageTime { get; set; }

    /// <summary>
    /// The unit of <see cref="PromotionDurationUsageTime"/>. Observed on the wire only as "PROMO_MINUTE";
    /// "PROMO_SECOND"/"PROMO_HOUR"/"PROMO_DAY" are handled by naming convention. Any other/unknown unit is
    /// kept verbatim here (so it is still visible in a dump) but yields a null <see cref="ReuseCooldown"/>.
    /// </summary>
    public string? PromotionDurationUsageType { get; set; }

    /// <summary>
    /// <see cref="PromotionDurationUsageTime"/> expressed as a <see cref="TimeSpan"/>, or null when there
    /// is no positive amount or the <see cref="PromotionDurationUsageType"/> unit is not recognised.
    /// The unit is parsed from the "PROMO_&lt;UNIT&gt;" token so a new unit is a one-line addition.
    /// </summary>
    [JsonIgnore]
    public TimeSpan? ReuseCooldown
    {
        get
        {
            if (PromotionDurationUsageTime is not { } amount || amount <= 0) return null;
            var type = PromotionDurationUsageType;
            if (string.IsNullOrWhiteSpace(type)) return null;

            // APCOA tokens are "PROMO_<UNIT>"; strip the prefix if present and switch on the unit.
            const string prefix = "PROMO_";
            var unit = type.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? type[prefix.Length..]
                : type;

            return unit.ToUpperInvariant() switch
            {
                "MINUTE" or "MINUTES" => TimeSpan.FromMinutes(amount), // confirmed on the wire
                "SECOND" or "SECONDS" => TimeSpan.FromSeconds(amount),
                "HOUR" or "HOURS" => TimeSpan.FromHours(amount),       // inferred by naming convention
                "DAY" or "DAYS" => TimeSpan.FromDays(amount),          // inferred by naming convention
                _ => null,                                             // unknown unit → don't guess
            };
        }
    }

    /// <summary>
    /// True when the promotion carries a positive cooldown amount with a <see cref="PromotionDurationUsageType"/>
    /// that was NOT recognised (so <see cref="ReuseCooldown"/> is null only because of the unit, not because the
    /// field was absent). Lets callers log the unmapped unit rather than silently ignoring it.
    /// </summary>
    [JsonIgnore]
    public bool HasUnrecognisedCooldownUnit =>
        PromotionDurationUsageTime is > 0
        && !string.IsNullOrWhiteSpace(PromotionDurationUsageType)
        && ReuseCooldown is null;
}
