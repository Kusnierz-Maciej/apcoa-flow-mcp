namespace ApcoaFlowApiClient.Models.Session;

public class StartParkingSessionRequest
{
    public int TariffId { get; set; }
    public int ParkingId { get; set; }
    public int ParkingLotId { get; set; }
    public string VehicleNumber { get; set; } = string.Empty;
    public string ParkingSessionType { get; set; } = "STP_BOOKING";
    public string EntryChannel { get; set; } = "CONSUMER";
    public string ProductType { get; set; } = "CONSUMER_APP";
    public string PaymentMethod { get; set; } = string.Empty;
    public int ConsumerUserPaymentId { get; set; }
    public int ConsumerProfileId { get; set; }
    public string SessionCheckInTime { get; set; } = string.Empty;
    public string? ScheduledSessionCheckInTime { get; set; }
    public string? ScheduledSessionCheckOutTime { get; set; }
    public string? PromotionName { get; set; }
    public int? PromotionId { get; set; }
    public string? OfferDiscountType { get; set; }
    public string? DiscountCardMasterName { get; set; }
    public bool IsAutoApplyPromotion { get; set; }
}
