namespace ApcoaFlowApiClient.Models.Ev;

/// <summary>Query params for the EV sessions list endpoint.</summary>
public class EvSessionQuery
{
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    /// <summary>Comma-separated: ACTIVE,RELEASED | COMPLETED</summary>
    public string StatusList { get; set; } = string.Empty;
}
