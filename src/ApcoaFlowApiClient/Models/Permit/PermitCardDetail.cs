namespace ApcoaFlowApiClient.Models.Permit;

public class PermitCardDetail
{
    public int ConsumerUserId { get; set; }
    public string? PaymentStatus { get; set; }
    public decimal? AmountDue { get; set; }
    public string? CurrencyCode { get; set; }
}
