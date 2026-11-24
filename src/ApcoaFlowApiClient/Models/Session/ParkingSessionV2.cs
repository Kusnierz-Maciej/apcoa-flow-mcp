namespace ApcoaFlowApiClient.Models.Session;

/// <summary>Represents a parking session as returned by the V2 endpoint.</summary>
public class ParkingSessionV2
{
    public long Id { get; set; }
    public int ParkingId { get; set; }
    public int ParkingLotId { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? EntryTime { get; set; }
    public string? ExitTime { get; set; }
    public decimal? TotalAmount { get; set; }
    public string? CurrencyCode { get; set; }
    public bool? DisplayVat { get; set; }
}
