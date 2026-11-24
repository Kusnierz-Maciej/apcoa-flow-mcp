using Refit;

namespace ApcoaFlowApiClient.Models.Ev;

/// <summary>Query params for the EV sessions list endpoint.</summary>
public class EvSessionQuery
{
    [AliasAs("pageNumber")]
    public int PageNumber { get; set; } = 1;
    [AliasAs("pageSize")]
    public int PageSize { get; set; } = 10;
    /// <summary>Comma-separated: ACTIVE,RELEASED | COMPLETED</summary>
    [AliasAs("statusList")]
    public string StatusList { get; set; } = string.Empty;
}
