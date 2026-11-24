namespace ApcoaFlowApiClient.Models.Session;

/// <summary>Query params for the V2 parking sessions list endpoint.</summary>
public class ParkingSessionV2Query
{
    public bool DisplayVat { get; set; } = false;
    public bool DisplayVatWithoutConvenienceFeeVat { get; set; } = false;
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public string ParkingSessionStatuses { get; set; } = string.Empty;
    public string ProfileNames { get; set; } = string.Empty;
    /// <summary>ACTIVE | COMPLETED | IN_REVIEW</summary>
    public string Status { get; set; } = string.Empty;
}
