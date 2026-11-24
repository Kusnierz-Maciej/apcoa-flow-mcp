using System.Text.Json.Serialization;
using ApcoaFlowApiClient.Converters;

namespace ApcoaFlowApiClient.Models.Session;

public class StopParkingSessionRequest
{
    public long ParkingSessionId { get; set; }
    /// <summary>Always "COMPLETED" in observed traffic.</summary>
    public string ParkingSessionStatus { get; set; } = "COMPLETED";

    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? SessionCheckOutTime { get; set; }

    public string ExitChannel { get; set; } = "CONSUMER";
}
