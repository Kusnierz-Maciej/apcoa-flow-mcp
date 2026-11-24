namespace ApcoaFlowApiClient.Models.Session;

public class ExtendParkingSessionRequest
{
    public long ParkingSessionId { get; set; }
    public int TariffId { get; set; }
    public int ConsumerUserPaymentId { get; set; }
    public string ExtensionStartTime { get; set; } = string.Empty;
    public string ExtensionEndTime { get; set; } = string.Empty;
}
