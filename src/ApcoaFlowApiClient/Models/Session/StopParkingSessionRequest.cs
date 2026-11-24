namespace ApcoaFlowApiClient.Models.Session;

public class StopParkingSessionRequest
{
    public long ParkingSessionId { get; set; }
    /// <summary>Always "COMPLETED" in observed traffic.</summary>
    public string ParkingSessionStatus { get; set; } = "COMPLETED";
    public string SessionCheckOutTime { get; set; } = string.Empty;
    public string ExitChannel { get; set; } = "CONSUMER";
}
