using System.Text.Json.Serialization;
using ApcoaFlowApiClient.Converters;

namespace ApcoaFlowApiClient.Models.Promotion;

public class ValidatePromotionRequest
{
    public int ParkingLot { get; set; }
    public string PromotionName { get; set; } = string.Empty;
    public string VehicleNumber { get; set; } = string.Empty;
    public int ConsumerID { get; set; }
    public string Company { get; set; } = string.Empty;
    public string Tenant { get; set; } = "apcoa";

    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? StartTime { get; set; }

    public string MobilePlatform { get; set; } = "iOS";

    /// <summary>Session duration (API sends an integer number of minutes, e.g. 300). Optional.</summary>
    [JsonConverter(typeof(NullableMinutesTimeSpanConverter))]
    public TimeSpan? DurationValue { get; set; }
}
