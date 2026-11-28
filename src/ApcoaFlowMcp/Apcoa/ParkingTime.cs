using ApcoaFlowApiClient;

namespace ApcoaFlowMcp.Apcoa;

/// <summary>
/// Times are about the parking, so they're read and shown in the parking's time zone, wherever the user is (someone
/// may start a session for a car on the other side of the world). The API gives no zone, only the parking's
/// <c>countryCode</c> ("PL"); every country APCOA Flow serves has a single zone. Unknown or missing → the app's zone
/// (Europe/Warsaw).
/// </summary>
public static class ParkingTime
{
    private static readonly Dictionary<string, string> Zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PL"] = "Europe/Warsaw", ["DE"] = "Europe/Berlin", ["AT"] = "Europe/Vienna", ["CH"] = "Europe/Zurich",
        ["NL"] = "Europe/Amsterdam", ["BE"] = "Europe/Brussels", ["LU"] = "Europe/Luxembourg", ["FR"] = "Europe/Paris",
        ["IT"] = "Europe/Rome", ["ES"] = "Europe/Madrid", ["DK"] = "Europe/Copenhagen", ["SE"] = "Europe/Stockholm",
        ["NO"] = "Europe/Oslo", ["CZ"] = "Europe/Prague", ["SK"] = "Europe/Bratislava", ["HU"] = "Europe/Budapest",
        ["GB"] = "Europe/London", ["UK"] = "Europe/London", ["IE"] = "Europe/Dublin", ["PT"] = "Europe/Lisbon",
        ["FI"] = "Europe/Helsinki", ["LT"] = "Europe/Vilnius", ["LV"] = "Europe/Riga", ["EE"] = "Europe/Tallinn",
    };

    /// <summary>The time zone of a parking in <paramref name="countryCode"/>.</summary>
    public static TimeZoneInfo Zone(string? countryCode)
    {
        if (countryCode is not null && Zones.TryGetValue(countryCode.Trim(), out var id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        }
        return ApcoaFlowEndpoints.AppTimeZone;
    }

    /// <summary>The zone's name when it isn't the app's (Polish) one, for "18:00 (Europe/Berlin time)"; else empty.</summary>
    public static string Suffix(TimeZoneInfo zone) =>
        zone.Id == ApcoaFlowEndpoints.AppTimeZone.Id ? "" : $" ({zone.Id} time)";
}
