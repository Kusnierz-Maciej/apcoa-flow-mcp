using System.Text.Json.Serialization;
using ApcoaFlowApiClient.Converters;

namespace ApcoaFlowApiClient.Models.Parking;

/// <summary>
/// One entry from <c>GET /parking/{id}/parkingLots</c>. The usable lot id and tariff ids live under
/// <see cref="NearbyParkingLot"/>; <see cref="ParkingLotConfigurationResponse"/> holds booking config.
/// </summary>
public class ParkingLot
{
    [JsonPropertyName("nearbyParkingLot")]
    public NearbyParkingLot? NearbyParkingLot { get; set; }

    [JsonPropertyName("parkingLotConfigurationResponse")]
    public ParkingLotConfiguration? ParkingLotConfigurationResponse { get; set; }

    [JsonPropertyName("freeDurationInMinutes")]
    [JsonConverter(typeof(MinutesTimeSpanConverter))]
    public TimeSpan FreeDuration { get; set; }

    /// <summary>Convenience: the lot id used for sessions/tariff calls.</summary>
    [JsonIgnore]
    public int? LotId => NearbyParkingLot?.Id;

    /// <summary>Convenience: the first (active) tariff id, used to start a session / quote a price.</summary>
    [JsonIgnore]
    public int? PrimaryTariffId => NearbyParkingLot?.TariffBasicInfoSet?.FirstOrDefault()?.TariffId;

    /// <summary>Convenience: the booking config of the first lot, if present.</summary>
    [JsonIgnore]
    public ParkingLotConfigItem? PrimaryConfig =>
        ParkingLotConfigurationResponse?.ParkingLotConfigurationList?.FirstOrDefault();

    /// <summary>Convenience: max session length, if the config exposes one.</summary>
    [JsonIgnore]
    public TimeSpan? MaxDuration => PrimaryConfig?.MaxDuration;
}

public class NearbyParkingLot
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public List<TariffBasicInfo> TariffBasicInfoSet { get; set; } = [];
}

public class TariffBasicInfo
{
    public string? TariffName { get; set; }
    public int TariffId { get; set; }
}

public class ParkingLotConfiguration
{
    public string? CurrencyCode { get; set; }
    public List<ParkingLotConfigItem> ParkingLotConfigurationList { get; set; } = [];
}

public class ParkingLotConfigItem
{
    public int Id { get; set; }

    [JsonConverter(typeof(MinutesTimeSpanConverter))]
    public TimeSpan MinDuration { get; set; }
    [JsonConverter(typeof(MinutesTimeSpanConverter))]
    public TimeSpan MaxDuration { get; set; }

    public string? SessionType { get; set; }
    public string? PaymentType { get; set; }
    public int AllowExtension { get; set; }

    [JsonPropertyName("minExtensionInMins")]
    [JsonConverter(typeof(MinutesTimeSpanConverter))]
    public TimeSpan MinExtension { get; set; }

    [JsonPropertyName("extensionIntervalInMins")]
    [JsonConverter(typeof(MinutesTimeSpanConverter))]
    public TimeSpan ExtensionInterval { get; set; }

    public string? BookingButtonText { get; set; }

    /// <summary>1 when an active session can be stopped early at all.</summary>
    public int EnableStopButton { get; set; }

    /// <summary>1 when stopping early refunds (fully or prorated); 0 = no refund (remaining time forfeited).</summary>
    public int AllowRefund { get; set; }
}
