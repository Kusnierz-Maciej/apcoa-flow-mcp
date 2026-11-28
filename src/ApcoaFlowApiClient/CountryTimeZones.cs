namespace ApcoaFlowApiClient;

/// <summary>
/// A parking's time zone from its <c>countryCode</c> ("PL"): the API gives no zone, and every country APCOA Flow serves
/// has a single one. 2026-11-28 the map (all ~5,500 pins in Europe, sampled one per 1° cell) had PL, SE, NO, GB, IT,
/// AT, DK, DE; the live test <c>Every_country_on_the_map_has_a_time_zone</c> keeps this table complete.
/// </summary>
public static class CountryTimeZones
{
    private static readonly Dictionary<string, string> Ids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PL"] = "Europe/Warsaw", ["DE"] = "Europe/Berlin", ["AT"] = "Europe/Vienna", ["CH"] = "Europe/Zurich",
        ["NL"] = "Europe/Amsterdam", ["BE"] = "Europe/Brussels", ["LU"] = "Europe/Luxembourg", ["FR"] = "Europe/Paris",
        ["IT"] = "Europe/Rome", ["ES"] = "Europe/Madrid", ["DK"] = "Europe/Copenhagen", ["SE"] = "Europe/Stockholm",
        ["NO"] = "Europe/Oslo", ["CZ"] = "Europe/Prague", ["SK"] = "Europe/Bratislava", ["HU"] = "Europe/Budapest",
        ["GB"] = "Europe/London", ["UK"] = "Europe/London", ["IE"] = "Europe/Dublin", ["PT"] = "Europe/Lisbon",
        ["FI"] = "Europe/Helsinki", ["LT"] = "Europe/Vilnius", ["LV"] = "Europe/Riga", ["EE"] = "Europe/Tallinn",
    };

    /// <summary>The zone for <paramref name="countryCode"/>; false when the country isn't in the table.</summary>
    public static bool TryGet(string? countryCode, out TimeZoneInfo zone)
    {
        zone = ApcoaFlowEndpoints.AppTimeZone;
        if (countryCode is null || !Ids.TryGetValue(countryCode.Trim(), out var id)) return false;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (TimeZoneNotFoundException) { return false; }
    }

    /// <summary>The zone for <paramref name="countryCode"/>, else the app's (Europe/Warsaw).</summary>
    public static TimeZoneInfo For(string? countryCode) => TryGet(countryCode, out var zone) ? zone : ApcoaFlowEndpoints.AppTimeZone;
}
