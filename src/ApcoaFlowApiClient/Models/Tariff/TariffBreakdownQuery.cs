using Refit;

namespace ApcoaFlowApiClient.Models.Tariff;

/// <summary>
/// Query params for the tariff breakdown endpoint. Build with <see cref="For"/> so callers pass
/// <see cref="DateTimeOffset"/>/<see cref="TimeSpan"/> and the wire formatting (US date format for
/// entry/exit, integer minutes for duration) is handled here.
/// NOTE: the server has a typo in the <c>vehicleNumbwe</c> param — it must be sent exactly as spelled.
/// </summary>
public class TariffBreakdownQuery
{
    /// <summary>ISO 3166-1 alpha-2 country code, e.g. "PL".</summary>
    [AliasAs("company")]
    public string Company { get; set; } = string.Empty;

    /// <summary>Duration in whole minutes (wire format).</summary>
    [AliasAs("duration")]
    public int Duration { get; set; }

    /// <summary>Entry time, formatted as the endpoint expects (<c>MM/dd/yyyy HH:mm</c>, UTC).</summary>
    [AliasAs("entryTime")]
    [Query(Format = "MM/dd/yyyy HH:mm")]
    public DateTimeOffset EntryTime { get; set; }

    /// <summary>Exit time, formatted as the endpoint expects (<c>MM/dd/yyyy HH:mm</c>, UTC).</summary>
    [AliasAs("exitTime")]
    [Query(Format = "MM/dd/yyyy HH:mm")]
    public DateTimeOffset ExitTime { get; set; }

    [AliasAs("parentTariffId")]
    public int ParentTariffId { get; set; }
    [AliasAs("parkingLotId")]
    public int ParkingLotId { get; set; }
    [AliasAs("productType")]
    public string ProductType { get; set; } = "CONSUMER_APP";
    [AliasAs("tariffId")]
    public int TariffId { get; set; }

    /// <summary>Server-side typo preserved: "vehicleNumbwe" not "vehicleNumber".</summary>
    [AliasAs("vehicleNumbwe")]
    public string VehicleNumbwe { get; set; } = string.Empty;

    [AliasAs("discountName")]
    public string? DiscountName { get; set; }
    [AliasAs("offerDiscountType")]
    public string? OfferDiscountType { get; set; }
    [AliasAs("promotionId")]
    public int? PromotionId { get; set; }
    [AliasAs("parkingSessionId")]
    public long? ParkingSessionId { get; set; }
    [AliasAs("consumerUserId")]
    public int? ConsumerUserId { get; set; }

    /// <summary>
    /// Builds a query from typed inputs: <paramref name="window"/> as a <see cref="TimeSpan"/> and UTC
    /// <paramref name="entry"/> time; duration minutes and exit time are derived.
    /// </summary>
    public static TariffBreakdownQuery For(
        int parkingLotId, int tariffId, DateTimeOffset entry, TimeSpan window, string plate,
        string company = "PL", int? promotionId = null, string? discountName = null,
        string? offerDiscountType = null, int? consumerUserId = null) => new()
    {
        Company = company,
        Duration = (int)window.TotalMinutes,
        EntryTime = entry.ToUniversalTime(),
        ExitTime = entry.Add(window).ToUniversalTime(),
        ParentTariffId = tariffId,
        ParkingLotId = parkingLotId,
        TariffId = tariffId,
        VehicleNumbwe = plate,
        PromotionId = promotionId,
        DiscountName = discountName,
        OfferDiscountType = offerDiscountType,
        ConsumerUserId = consumerUserId,
    };
}
