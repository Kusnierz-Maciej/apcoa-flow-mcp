namespace ApcoaFlowApiClient.Models.Permit;

public class PermitAllocation
{
    public long Id { get; set; }
    public int ConsumerId { get; set; }
    public string? Status { get; set; }
    public int? ParkingId { get; set; }
    public string? ValidFrom { get; set; }
    public string? ValidTo { get; set; }
}
