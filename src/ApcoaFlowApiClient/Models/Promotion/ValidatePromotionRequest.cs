namespace ApcoaFlowApiClient.Models.Promotion;

public class ValidatePromotionRequest
{
    public int ParkingLot { get; set; }
    public string PromotionName { get; set; } = string.Empty;
    public string VehicleNumber { get; set; } = string.Empty;
    public int ConsumerID { get; set; }
    public string Company { get; set; } = string.Empty;
    public string Tenant { get; set; } = "apcoa";
    public string StartTime { get; set; } = string.Empty;
    public string MobilePlatform { get; set; } = "iOS";
}
