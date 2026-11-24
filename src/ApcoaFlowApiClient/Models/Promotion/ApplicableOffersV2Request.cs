namespace ApcoaFlowApiClient.Models.Promotion;

/// <summary>Request body for <c>POST /promotion/applicableOffersWithPromotionV2</c>.</summary>
public class ApplicableOffersV2Request
{
    public int ParkingLot { get; set; }
    public int UserId { get; set; }
    public string Company { get; set; } = "PL";
    public bool IsActiveFlagRequired { get; set; } = true;
    public bool IsAutoApply { get; set; }
    public int IsDisplay { get; set; } = 1;
    public bool CheckDurationBasedPromotion { get; set; }
    public int PageSize { get; set; } = 10;
    public int PageNumber { get; set; } = 1;
}
