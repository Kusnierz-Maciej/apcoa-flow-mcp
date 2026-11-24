namespace ApcoaFlowApiClient.Models.Promotion;

/// <summary>Paginated response of <c>POST /promotion/applicableOffersWithPromotionV2</c>.</summary>
public class OffersResponse
{
    public int Count { get; set; }
    public int Total { get; set; }
    public List<Offer> Entities { get; set; } = [];
}
