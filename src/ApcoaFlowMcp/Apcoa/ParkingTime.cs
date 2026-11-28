using System.Collections.Concurrent;
using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Models;

namespace ApcoaFlowMcp.Apcoa;

/// <summary>
/// Times are about the parking, so they're read and shown in the parking's time zone, wherever the user is (someone
/// may start a session for a car on the other side of the world). The API gives times in UTC and no zone, only a
/// parking's <c>countryCode</c> (<see cref="CountryTimeZones"/>). A parking's country doesn't change, so its zone is
/// kept in memory, learnt <b>whenever a parking is seen</b>: every details call (<see cref="GetParkingAsync"/>) and
/// every search result. Sessions carry the parking's id but not its country: <see cref="GetZoneAsync"/> fills a gap
/// with one details call.
/// </summary>
public static class ParkingTime
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo> Known = new();

    /// <summary>The time zone of a parking in <paramref name="countryCode"/> (Warsaw when unknown).</summary>
    public static TimeZoneInfo Zone(string? countryCode) => CountryTimeZones.For(countryCode);

    /// <summary>Notes the zone of the parking <paramref name="parkingId"/> and returns it.</summary>
    public static TimeZoneInfo Remember(string? parkingId, string? countryCode)
    {
        var zone = Zone(countryCode);
        if (!string.IsNullOrEmpty(parkingId) && countryCode is not null) Known[parkingId] = zone;
        return zone;
    }

    /// <summary>
    /// A parking's details — the one way this server fetches them, so every parking seen has its zone remembered.
    /// Pass <paramref name="priceOptions"/> for the hourly price rows (<see cref="TariffTable"/>).
    /// </summary>
    public static async Task<SpaceDetails> GetParkingAsync(this IApcoaFlowApi api, string parkingId,
        IEnumerable<int>? priceOptions, CancellationToken ct)
    {
        var details = await api.GetSpaceAsync(parkingId, priceOptions, ct);
        Remember(details.Id ?? parkingId, details.CountryCode);
        return details;
    }

    /// <summary>The zone of a parking: from memory, else from its details (one call); Warsaw if that fails.</summary>
    public static async Task<TimeZoneInfo> GetZoneAsync(IApcoaFlowApi api, string? parkingId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(parkingId)) return ApcoaFlowEndpoints.AppTimeZone;
        if (Known.TryGetValue(parkingId, out var zone)) return zone;
        try { return Zone((await api.GetParkingAsync(parkingId, null, ct)).CountryCode); }
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
