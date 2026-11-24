namespace ApcoaFlowApiClient.Models.Pass;

public class PassAllocation
{
    public long Id { get; set; }
    public int ConsumerId { get; set; }
    public string? Status { get; set; }
    public string? ProfileName { get; set; }
    public string? ValidFrom { get; set; }
    public string? ValidTo { get; set; }
}
