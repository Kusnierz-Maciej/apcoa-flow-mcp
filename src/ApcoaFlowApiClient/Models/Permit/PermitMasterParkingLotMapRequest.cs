namespace ApcoaFlowApiClient.Models.Permit;

public class PermitMasterParkingLotMapRequest
{
    public List<int> ParkingLotIdList { get; set; } = [];
    /// <summary>Sent as string "true" in observed traffic.</summary>
    public string BreakupRequired { get; set; } = "true";
    public int PermitMasterRecordsLimit { get; set; } = 5;
}
