using System.Collections.Concurrent;
using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Api;

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

    // Parking id → zone, learnt from every details call (a parking's country doesn't change). Sessions carry the
    // parking's id but not its country, and the API gives their times in UTC.
    private static readonly ConcurrentDictionary<string, TimeZoneInfo> Known = new();

    /// <summary>Notes the zone of the parking <paramref name="spaceId"/> (from its details) and returns it.</summary>
    public static TimeZoneInfo Remember(string? spaceId, string? countryCode)
    {
        var zone = Zone(countryCode);
        if (!string.IsNullOrEmpty(spaceId)) Known[spaceId] = zone;
        return zone;
    }

    /// <summary>The zone of a parking seen before; the app's zone otherwise.</summary>
    public static TimeZoneInfo Of(string? spaceId) =>
        spaceId is not null && Known.TryGetValue(spaceId, out var zone) ? zone : ApcoaFlowEndpoints.AppTimeZone;

    /// <summary>The zone of a parking, from its details when not seen yet (one call); the app's zone if that fails.</summary>
    public static async Task<TimeZoneInfo> OfAsync(IApcoaFlowApi api, string? spaceId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(spaceId)) return ApcoaFlowEndpoints.AppTimeZone;
        if (Known.TryGetValue(spaceId, out var zone)) return zone;
        try { return Remember(spaceId, (await api.GetSpaceAsync(spaceId, ct: ct)).CountryCode); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ApcoaFlowEndpoints.AppTimeZone; }
    }

    /// <summary>The time in the zone of the parking <paramref name="spaceId"/>, named when it isn't Polish time.</summary>
    public static string At(DateTimeOffset utc, TimeProvider time, string? spaceId)
    {
        var zone = Of(spaceId);
        return Text.LocalTime(utc, time, zone) + Suffix(zone);
    }

    /// <summary>The zone's name when it isn't the app's (Polish) one, for "18:00 (Europe/Berlin time)"; else empty.</summary>
    public static string Suffix(TimeZoneInfo zone) =>
        zone.Id == ApcoaFlowEndpoints.AppTimeZone.Id ? "" : $" ({zone.Id} time)";
}
