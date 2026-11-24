using System.Text.Json.Serialization;
using ApcoaFlowApiClient.Converters;

namespace ApcoaFlowApiClient.Models.Ev;

public class EvSession
{
    public long Id { get; set; }
    public string Status { get; set; } = string.Empty;

    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? StartTime { get; set; }
    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? EndTime { get; set; }

    public decimal? TotalAmount { get; set; }
    public string? CurrencyCode { get; set; }
    public decimal? EnergyDeliveredKwh { get; set; }
}
