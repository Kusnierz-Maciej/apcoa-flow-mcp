using ApcoaFlowApiClient.Models;
using GeoTimeZone;

namespace ApcoaFlowApiClient;

/// <summary>
/// A parking's time zone from its coordinates (the API gives no zone): an offline lookup in the IANA time-zone
/// boundaries (GeoTimeZone), exact even in countries with several zones. Pins, search results and details all carry
/// coordinates. The live test <c>Every_parking_on_the_map_has_a_time_zone</c> checks every pin resolves.
/// </summary>
public static class ParkingTimeZones
{
    /// <summary>The zone at a position; null if the lookup gives a zone this system doesn't know.</summary>
    public static TimeZoneInfo? At(double latitude, double longitude)
    {
        var id = TimeZoneLookup.GetTimeZone(latitude, longitude).Result;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return null; }
    }

    /// <summary>The zone of a parking at <paramref name="coordinates"/>, else the app's (Europe/Warsaw).</summary>
    public static TimeZoneInfo For(Coordinates? coordinates) =>
        coordinates is null ? ApcoaFlowEndpoints.AppTimeZone : At(coordinates.Latitude, coordinates.Longitude) ?? ApcoaFlowEndpoints.AppTimeZone;
}
