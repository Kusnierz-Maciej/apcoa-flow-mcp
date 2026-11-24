namespace ApcoaFlowApiClient.Models.Parking;

public class ParkingSession
{
    public string Id { get; set; } = string.Empty;
    public string ZoneId { get; set; } = string.Empty;
    public string LicensePlate { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public bool IsActive => EndedAt is null;
}
