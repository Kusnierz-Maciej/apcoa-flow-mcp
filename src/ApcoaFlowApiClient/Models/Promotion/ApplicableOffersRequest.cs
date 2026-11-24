namespace ApcoaFlowApiClient.Models.Promotion;

public class ApplicableOffersRequest
{
    public int ParkingLot { get; set; }
    /// <summary>License plate / vehicle identifier.</summary>
    public string Identifier { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public string Tenant { get; set; } = "apcoa";
    public bool IsAutoApply { get; set; } = true;
    public bool CheckDurationBasedPromotion { get; set; } = true;
}
