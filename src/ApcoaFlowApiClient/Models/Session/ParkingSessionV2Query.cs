using Refit;

namespace ApcoaFlowApiClient.Models.Session;

/// <summary>Query params for the V2 parking sessions list endpoint.</summary>
public class ParkingSessionV2Query
{
    [AliasAs("displayVat")]
    public bool DisplayVat { get; set; } = false;
    [AliasAs("displayVatWithoutConvenienceFeeVat")]
    public bool DisplayVatWithoutConvenienceFeeVat { get; set; } = false;
    [AliasAs("pageNumber")]
    public int PageNumber { get; set; } = 1;
    [AliasAs("pageSize")]
    public int PageSize { get; set; } = 10;
    [AliasAs("parkingSessionStatuses")]
    public string ParkingSessionStatuses { get; set; } = string.Empty;
    [AliasAs("profileNames")]
    public string ProfileNames { get; set; } = string.Empty;
    /// <summary>ACTIVE | COMPLETED | IN_REVIEW</summary>
    [AliasAs("status")]
    public string Status { get; set; } = string.Empty;
}
