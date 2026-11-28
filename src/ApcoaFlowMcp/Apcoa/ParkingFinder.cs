using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Models;

namespace ApcoaFlowMcp.Apcoa;

/// <summary>A parking found by <see cref="ParkingFinder"/>.</summary>
/// <param name="DistanceMeters">From the given position (null without one).</param>
/// <param name="Inside">The position is inside the parking's outline.</param>
/// <param name="FromOutline">The distance is to the parking's outline, not its pin.</param>
public sealed record FoundParking(string Id, string Name, string? Address, string? Code, double? DistanceMeters,
    bool Inside, bool FromOutline);

/// <summary>
/// Finds parkings the way the APCOA Flow app does: <c>search</c> for text (with the position when there is one), the
/// map's bounding box for a position alone. Near a position the distance is to the parking's outline when it has one
/// (0 inside it), else to its pin.
/// </summary>
public sealed class ParkingFinder(IApcoaFlowApi api)
{
    public const int MaxResults = 20;

    /// <summary>
    /// Where a text search without the user's position is anchored. A position-less <c>search</c> returns parkings
    /// that aren't on the map (no coordinates, no address — also not in their details; 2026-11-27); with a position
    /// it returns the mapped ones. So a name-only query searches around the middle of Poland and hides the distance.
    /// </summary>
    public static readonly Coordinates SearchAnchor = new(52.0, 19.0);

    /// <summary>Parkings within <paramref name="radiusMeters"/> of a position, nearest first.</summary>
    public async Task<List<FoundParking>> NearAsync(double lat, double lon, double radiusMeters, CancellationToken ct)
    {
        var (swLat, swLon, neLat, neLon) = Geo.Box(lat, lon, radiusMeters);
        var pins = await api.GetSpacesByBoundingBoxAsync(new BoundingBoxQuery
        {
            SwLatitude = swLat, SwLongitude = swLon, NeLatitude = neLat, NeLongitude = neLon, Zoom = 16, // zoom changes nothing
        }, ct);

        return pins
            .Select(p => (Pin: p, Distance: Geo.To(lat, lon, p.Coordinates, p.GeoBorder)))
            .Where(x => x.Distance is { } d && d.Meters <= radiusMeters)
            .OrderBy(x => x.Distance!.Value.Meters)
            .Take(MaxResults)
            .Select(x => new FoundParking(x.Pin.Id, x.Pin.Name, null, null, x.Distance!.Value.Meters,
                x.Distance.Value.Inside, x.Distance.Value.FromOutline))
            .ToList();
    }

    /// <summary>
    /// Text search through the API's <c>search</c>: name, brand, town, street, postcode or location code. With a
    /// position the API ranks nearest first; hits within <paramref name="outlineRadiusMeters"/> are then measured to
    /// their outline (from the map pins around the position) instead of the API's distance to the pin.
    /// </summary>
    public async Task<List<FoundParking>> SearchAsync(string query, double? lat, double? lon, double outlineRadiusMeters, CancellationToken ct)
    {
        var response = await api.SearchAsync(new SearchQuery
        {
            Query = query, Latitude = lat ?? SearchAnchor.Latitude, Longitude = lon ?? SearchAnchor.Longitude,
            Size = MaxResults, IsParking = true, IsCharging = false,
        }, ct);

        Dictionary<string, MapSpace> pins = [];
        if (lat is not null && lon is not null && response.Spaces.Count > 0)
        {
            var (swLat, swLon, neLat, neLon) = Geo.Box(lat.Value, lon.Value, outlineRadiusMeters);
            pins = (await api.GetSpacesByBoundingBoxAsync(new BoundingBoxQuery
            {
                SwLatitude = swLat, SwLongitude = swLon, NeLatitude = neLat, NeLongitude = neLon, Zoom = 16,
            }, ct)).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        }

        foreach (var s in response.Spaces)
            ParkingTime.Remember(s.Id, s.CountryCode); // every parking seen: remember its time zone
        var found = response.Spaces.Take(MaxResults).Select(s =>
        {
            var code = string.IsNullOrWhiteSpace(s.TariffCode) ? null : s.TariffCode;
            if (pins.TryGetValue(s.Id, out var pin) && Geo.To(lat!.Value, lon!.Value, pin.Coordinates, pin.GeoBorder) is { } d)
                return new FoundParking(s.Id, s.Name, Address(s.Address?.DisplayValue), code, d.Meters, d.Inside, d.FromOutline);
            return new FoundParking(s.Id, s.Name, Address(s.Address?.DisplayValue), code,
                lat is not null && s.Distance is { } km ? km * 1000 : null, Inside: false, FromOutline: false);
        }).ToList();
        return lat is null ? found : found.OrderBy(f => f.DistanceMeters ?? double.MaxValue).ToList();
    }

    /// <summary>The map pin of one parking (for its outline and type): the pins in a small box around its coordinates.</summary>
    public async Task<MapSpace?> PinAsync(string parkingId, Coordinates? at, CancellationToken ct)
    {
        if (at is null) return null;
        var (swLat, swLon, neLat, neLon) = Geo.Box(at.Latitude, at.Longitude, 300);
        var pins = await api.GetSpacesByBoundingBoxAsync(new BoundingBoxQuery
        {
            SwLatitude = swLat, SwLongitude = swLon, NeLatitude = neLat, NeLongitude = neLon, Zoom = 16,
        }, ct);
        return pins.FirstOrDefault(p => p.Id == parkingId);
    }

    // The API pads missing parts: ", Strzegom, Poland".
    private static string? Address(string? display)
    {
        var trimmed = display?.Trim().TrimStart(',').Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
