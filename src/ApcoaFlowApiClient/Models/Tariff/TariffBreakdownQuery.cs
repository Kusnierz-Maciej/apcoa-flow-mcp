namespace ApcoaFlowApiClient.Models.Tariff;

/// <summary>
/// Query params for the tariff breakdown endpoint.
/// NOTE: The server has a typo in the <c>vehicleNumbwe</c> param — it must be sent exactly as spelled.
/// See API-NOTES.md for details.
/// </summary>
public class TariffBreakdownQuery
{
    /// <summary>ISO 3166-1 alpha-2 country code, e.g. "PL".</summary>
    public string Company { get; set; } = string.Empty;
    public int Duration { get; set; }
    public string EntryTime { get; set; } = string.Empty;
    public string ExitTime { get; set; } = string.Empty;
    public int ParentTariffId { get; set; }
    public int ParkingLotId { get; set; }
    public string ProductType { get; set; } = "CONSUMER_APP";
    public int TariffId { get; set; }
    /// <summary>Server-side typo preserved: "vehicleNumbwe" not "vehicleNumber".</summary>
    public string VehicleNumbwe { get; set; } = string.Empty;
    public string? DiscountName { get; set; }
    public string? OfferDiscountType { get; set; }
    public int? PromotionId { get; set; }
    public long? ParkingSessionId { get; set; }
    public int? ConsumerUserId { get; set; }
}
