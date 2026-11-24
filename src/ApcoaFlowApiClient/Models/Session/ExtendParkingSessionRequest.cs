using System.Text.Json.Serialization;
using ApcoaFlowApiClient.Converters;

namespace ApcoaFlowApiClient.Models.Session;

public class ExtendParkingSessionRequest
{
    public long ParkingSessionId { get; set; }
    public int TariffId { get; set; }
    public int ConsumerUserPaymentId { get; set; }

    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? ExtensionStartTime { get; set; }
    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? ExtensionEndTime { get; set; }
}
