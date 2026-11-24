namespace ApcoaFlowApiClient.Models.Promotion;

public class Offer
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? DiscountType { get; set; }
    public string? DiscountName { get; set; }
    public int? DiscountMinutes { get; set; }
}
