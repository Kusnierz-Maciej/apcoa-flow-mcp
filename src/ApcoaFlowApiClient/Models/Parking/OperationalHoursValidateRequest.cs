using System.Text.Json.Serialization;
using ApcoaFlowApiClient.Converters;

namespace ApcoaFlowApiClient.Models.Parking;

public class OperationalHoursValidateRequest
{
    public int ParkingId { get; set; }

    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? CheckInTime { get; set; }

    /// <summary>Optional — omitted on the first "is it open now" check, included for duration validation.</summary>
    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? CheckOutTime { get; set; }
}
