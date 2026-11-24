namespace ApcoaFlowApiClient.Models.Permit;

public class PermitAllocationQuery
{
    public int ConsumerUserId { get; set; }
    public string Direction { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public string PermitAllocationStatus { get; set; } = string.Empty;
    public int Size { get; set; } = 10;
    public string Sort { get; set; } = string.Empty;
}
