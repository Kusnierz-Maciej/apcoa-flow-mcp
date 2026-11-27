using System.Text.Json;
using System.Text.Json.Serialization;
using ApcoaFlowApiClient.Http;

namespace ApcoaFlowApiClient.Models;

/// <summary>A point, as every API payload writes it.</summary>
public sealed record Coordinates(double Latitude, double Longitude);

/// <summary>
/// A space's outline on the map, when it has one (78 of 525 PL spaces, 2026-11-27): a closed ring (last point = first,
/// sometimes repeated) of <c>{latitude, longitude}</c>, plus a display colour (null so far).
/// </summary>
public sealed class GeoBorder
{
    public List<Coordinates> Coordinates { get; set; } = [];
    public string? Color { get; set; }
}

/// <summary>
/// <c>GET /api/v1/spaces/getByBoundingBox?NELatitude&amp;NELongitude&amp;SWLatitude&amp;SWLongitude&amp;Zoom</c> item: a map
/// pin. Ids are GUIDs.
/// </summary>
public sealed class MapSpace
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Coordinates? Coordinates { get; set; }

    /// <summary><c>startStop</c> (523 of 525) or <c>anpr</c>.</summary>
    public string? ParkingType { get; set; }

    public string? ChargingType { get; set; }
    public GeoBorder? GeoBorder { get; set; }
    public bool IsMunicipality { get; set; }

    /// <summary>Fields not modelled yet, kept so sandbox dumps of the parsed objects show them.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary><c>GET /api/v1/search?query&amp;latitude&amp;longitude&amp;size&amp;isParking&amp;isCharging</c>.</summary>
public sealed class SearchResponse
{
    public List<SearchSpace> Spaces { get; set; } = [];
    public List<SearchPlace> Places { get; set; } = [];
    public List<JsonElement> Chargers { get; set; } = [];
}

/// <summary>
/// A space hit of <c>search</c>. <c>tariffCode</c> is the "location code" users type (a string here, empty for some).
/// No polygon in search hits (2026-11-27): only <c>getByBoundingBox</c> has <c>geoBorder</c>.
/// </summary>
public sealed class SearchSpace
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    [JsonConverter(typeof(StringOrNumberConverter))]
    public string? TariffCode { get; set; }

    public Coordinates? Coordinates { get; set; }
    public SpaceAddress? Address { get; set; }

    /// <summary>Kilometres from the given position (null without one).</summary>
    public double? Distance { get; set; }

    public string? CountryCode { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A geocoded place hit of <c>search</c> (street, town), not a space.</summary>
public sealed class SearchPlace
{
    public string Name { get; set; } = string.Empty;
    public Coordinates? Coordinates { get; set; }
    public SpaceAddress? Address { get; set; }

    /// <summary>Kilometres from the given position (1.689 for a street 1.7 km away, 2026-11-27).</summary>
    public double? Distance { get; set; }

    public string? CountryCode { get; set; }
}

public sealed class SpaceAddress
{
    public string? DisplayValue { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// <c>GET /api/v1/spaces/{spaceId}</c>: the space screen. No <c>geoBorder</c> here (only the bbox pins carry it).
/// <c>prices</c> is the human-readable tariff table, not a quote. Seen 2026-11-27 (Biedronka Opole):
/// <c>maxDuration: 43200</c> (minutes: 30 days) and <c>defaultLengthOfStayInMinutes: 90</c> (= the free 1.5 h).
/// Across 45 sampled parkings: <c>parkingOverview</c> titles "Open" (all) and "AutoPay" (plate-recognition parkings,
/// <c>sessionsStartAutomatically</c>); <c>ctaButtonType</c> "startParking" or "none" (can't start a session in the app);
/// some pins answer <c>404 FaTariffNotFound</c> ("No tariff available…").
/// </summary>
public sealed class SpaceDetails
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    [JsonConverter(typeof(StringOrNumberConverter))]
    public string? TariffCode { get; set; }

    /// <summary>Longest stay in minutes (43200 = 30 days, 2026-11-27); null = no limit. Quotes don't enforce it.</summary>
    public int? MaxDuration { get; set; }

    /// <summary>The app's preselected duration (Biedronka: 90 = its free window).</summary>
    public int? DefaultLengthOfStayInMinutes { get; set; }

    public Coordinates? Coordinates { get; set; }
    public SpaceAddress? Address { get; set; }

    /// <summary>Status lines, e.g. <c>{title: "Open", imageUrl}</c>.</summary>
    public List<SpaceOverviewItem> ParkingOverview { get; set; } = [];

    public bool SessionsStartAutomatically { get; set; }
    public List<SpacePriceSection> Prices { get; set; } = [];
    public List<string> AllowedPSPs { get; set; } = [];
    public bool HasLengthOfStayPreset { get; set; }

    /// <summary><c>startParking</c> for spaces the app can start a session on.</summary>
    public string? CtaButtonType { get; set; }

    public bool IsMunicipality { get; set; }
    public string? CountryCode { get; set; }
    public string? Disclaimer { get; set; }

    /// <summary>EV charging can be started here.</summary>
    public bool CanStartCharging { get; set; }

    /// <summary>Season tickets / contracts can be bought here (18 of 45 sampled, 2026-11-27).</summary>
    public bool HasAvailableContracts { get; set; }

    public bool HasPrebooking { get; set; }
    public bool HasFlexiPermits { get; set; }
    public int? GateFutureEntryAllowedMinutes { get; set; }
    public string? ParkingIconUrl { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SpaceOverviewItem
{
    public string? Title { get; set; }
    public string? ImageUrl { get; set; }
}

public sealed class SpacePriceSection
{
    public string? SectionName { get; set; }
    public List<PriceItem> Prices { get; set; } = [];
    public string? Description { get; set; }
}
