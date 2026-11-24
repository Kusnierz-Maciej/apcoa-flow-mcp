namespace ApcoaFlowApiClient.Models.Permit;

public class ActivePermitCountQuery
{
    public int ConsumerId { get; set; }
    /// <summary>e.g. "ACTIVE"</summary>
    public string PassMasterAllocationStatus { get; set; } = "ACTIVE";
}
