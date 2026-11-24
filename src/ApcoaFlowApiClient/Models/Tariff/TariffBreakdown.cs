using System.Text.Json.Serialization;
using ApcoaFlowApiClient.Converters;

namespace ApcoaFlowApiClient.Models.Tariff;

/// <summary>
/// Response of <c>GET /tariff/tariffBreakDownV2</c> — the price simulator. Amounts are integer cents.
/// <see cref="TotalPayableAmountInCents"/> is the authoritative total the user pays. A promotion-applied
/// quote sets <see cref="DiscountApplicable"/> = 1 and can drop the total to 0.
/// </summary>
public class TariffBreakdown
{
    public decimal AmountInCents { get; set; }
    public string? CurrencyCode { get; set; }

    /// <summary>Authoritative total the user pays, in integer cents.</summary>
    public int TotalPayableAmountInCents { get; set; }
    public int TotalPayableAmountInCentsWithoutDiscount { get; set; }
    public int AmountInCentsWithDiscount { get; set; }

    /// <summary>1 when a discount/promotion was applied to this quote.</summary>
    public int DiscountApplicable { get; set; }
    public int TotalDiscountAmount { get; set; }

    public int TotalVatAmount { get; set; }
    public double VatRate { get; set; }

    /// <summary>Quoted duration.</summary>
    [JsonConverter(typeof(MinutesTimeSpanConverter))]
    public TimeSpan Duration { get; set; }

    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? EntryTime { get; set; }
    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? ExitTime { get; set; }

    // Free-duration fields exist in the schema but are 0/empty in PL even for free parkings —
    // do not rely on them to detect "free"; use TotalPayableAmountInCents == 0 instead.
    public bool FreeDurationApplied { get; set; }

    [JsonConverter(typeof(MinutesTimeSpanConverter))]
    public TimeSpan FreeDuration { get; set; }

    [JsonConverter(typeof(MinutesTimeSpanConverter))]
    public TimeSpan ConsumedFreeDuration { get; set; }

    /// <summary>Server-side typo preserved from the API ("maximun"). Zero when unset.</summary>
    [JsonPropertyName("maximunSessionLimitInMinutes")]
    [JsonConverter(typeof(MinutesTimeSpanConverter))]
    public TimeSpan MaximumSessionLimit { get; set; }

    /// <summary>The amount in PLN (TotalPayableAmountInCents / 100).</summary>
    [JsonIgnore]
    public decimal TotalPayablePln => TotalPayableAmountInCents / 100m;

    /// <summary>True when this quote costs nothing — the reliable "is free" signal.</summary>
    [JsonIgnore]
    public bool IsFree => TotalPayableAmountInCents == 0;
}
