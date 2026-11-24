namespace ApcoaFlowApiClient.Models.Ev;

public class EvSession
{
    public long Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    public decimal? TotalAmount { get; set; }
    public string? CurrencyCode { get; set; }
    public decimal? EnergyDeliveredKwh { get; set; }
}
