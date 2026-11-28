using System.Collections.Concurrent;
using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Models;

namespace ApcoaFlowMcp.Apcoa;

/// <summary>
/// Times are about the parking, so they're read and shown in the parking's time zone, wherever the user is (someone
/// may start a session for a car on the other side of the world). The API gives times in UTC and no zone; the zone
/// comes from the parking's coordinates (<see cref="ParkingTimeZones"/>, offline). A parking doesn't move, so its zone
/// is kept in memory, learnt <b>whenever a parking is seen</b>: every details call (<see cref="GetParkingAsync"/>),
/// search result and map pin. Sessions carry the parking's id but no position: <see cref="GetZoneAsync"/> fills a gap
/// with one details call.
/// </summary>
public static class ParkingTime
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo> Known = new();

    /// <summary>The time zone of a parking at <paramref name="coordinates"/> (Warsaw when unknown).</summary>
    public static TimeZoneInfo Zone(Coordinates? coordinates) => ParkingTimeZones.For(coordinates);

    /// <summary>Notes the zone of the parking <paramref name="parkingId"/> at <paramref name="coordinates"/> and returns it.</summary>
    public static TimeZoneInfo Remember(string? parkingId, Coordinates? coordinates)
    {
        if (string.IsNullOrEmpty(parkingId) || coordinates is null) return Zone(coordinates);
        return Known.GetOrAdd(parkingId, _ => Zone(coordinates));
    }

    /// <summary>
    /// A parking's details — the one way this server fetches them, so every parking seen has its zone remembered.
    /// Pass <paramref name="priceOptions"/> for the hourly price rows (<see cref="TariffTable"/>).
    /// </summary>
    public static async Task<SpaceDetails> GetParkingAsync(this IApcoaFlowApi api, string parkingId,
        IEnumerable<int>? priceOptions, CancellationToken ct)
    {
        var details = await api.GetSpaceAsync(parkingId, priceOptions, ct);
        Remember(details.Id ?? parkingId, details.Coordinates);
        return details;
    }

    /// <summary>The zone of a parking: from memory, else from its details (one call); Warsaw if that fails.</summary>
    public static async Task<TimeZoneInfo> GetZoneAsync(IApcoaFlowApi api, string? parkingId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(parkingId)) return ApcoaFlowEndpoints.AppTimeZone;
        if (Known.TryGetValue(parkingId, out var zone)) return zone;
        try { return Zone((await api.GetParkingAsync(parkingId, null, ct)).Coordinates); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ApcoaFlowEndpoints.AppTimeZone; }
    }

    /// <summary>The zone of a parking seen before; Warsaw otherwise.</summary>
    public static TimeZoneInfo Of(string? parkingId) =>
        parkingId is not null && Known.TryGetValue(parkingId, out var zone) ? zone : ApcoaFlowEndpoints.AppTimeZone;

    /// <summary>The time in the zone of the parking <paramref name="parkingId"/>, named when it isn't Polish time.</summary>
    public static string At(DateTimeOffset utc, TimeProvider time, string? parkingId)
    {
        var zone = Of(parkingId);
        return Text.LocalTime(utc, time, zone) + Suffix(zone);
    }

    /// <summary>The zone's name when it isn't the app's (Polish) one, for "18:00 (Europe/Berlin time)"; else empty.</summary>
    public static string Suffix(TimeZoneInfo zone) =>
        zone.Id == ApcoaFlowEndpoints.AppTimeZone.Id ? "" : $" ({zone.Id} time)";
}
