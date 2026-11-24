namespace ApcoaFlowApiClient.Models.Parking;

public class StartSessionRequest
{
    public string ZoneId { get; set; } = string.Empty;
    public string LicensePlate { get; set; } = string.Empty;
}
