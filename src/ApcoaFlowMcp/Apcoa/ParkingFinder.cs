using System.Globalization;
using System.Text;
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
/// Finds parkings the way the APCOA Flow app does: <c>search</c> for text, the map's bounding box for a position. Near
/// a position the ranking is by distance to the parking's outline when it has one (0 inside it), else to its pin.
/// </summary>
public sealed class ParkingFinder(IApcoaFlowApi api)
{
    public const int MaxResults = 20;

    /// <summary>Parkings within <paramref name="radiusMeters"/> of a position, nearest first, optionally by name.</summary>
    public async Task<List<FoundParking>> NearAsync(double lat, double lon, double radiusMeters, string? nameContains, CancellationToken ct)
    {
        var (swLat, swLon, neLat, neLon) = Geo.Box(lat, lon, radiusMeters);
        var pins = await api.GetSpacesByBoundingBoxAsync(new BoundingBoxQuery
        {
            SwLatitude = swLat, SwLongitude = swLon, NeLatitude = neLat, NeLongitude = neLon, Zoom = 16, // zoom changes nothing
        }, ct);

        var wanted = Fold(nameContains);
        return pins
            .Where(p => wanted.Length == 0 || Fold(p.Name).Contains(wanted, StringComparison.Ordinal))
            .Select(p => (Pin: p, Distance: Geo.To(lat, lon, p.Coordinates, p.GeoBorder)))
            .Where(x => x.Distance is { } d && d.Meters <= radiusMeters)
            .OrderBy(x => x.Distance!.Value.Meters)
            .Take(MaxResults)
            .Select(x => new FoundParking(x.Pin.Id, x.Pin.Name, null, null, x.Distance!.Value.Meters,
                x.Distance.Value.Inside, x.Distance.Value.FromOutline))
            .ToList();
    }

    /// <summary>
    /// Text search: name, brand, town, postcode or location code. With a position the API ranks nearest first
    /// (distance from its km value).
    /// </summary>
    public async Task<List<FoundParking>> SearchAsync(string query, double? lat, double? lon, CancellationToken ct)
    {
        var response = await api.SearchAsync(new SearchQuery
        {
            Query = query, Latitude = lat, Longitude = lon, Size = MaxResults, IsParking = true, IsCharging = false,
        }, ct);
        return response.Spaces
            .Take(MaxResults)
            .Select(s => new FoundParking(s.Id, s.Name, Address(s.Address?.DisplayValue), string.IsNullOrWhiteSpace(s.TariffCode) ? null : s.TariffCode,
                s.Distance is { } km ? km * 1000 : null, Inside: false, FromOutline: false))
            .ToList();
    }

    // The API pads missing parts: ", Strzegom, Poland".
    private static string? Address(string? display)
    {
        var trimmed = display?.Trim().TrimStart(',').Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>Case- and accent-insensitive form for name matching ("Ostrów" ~ "ostrow").</summary>
    internal static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var decomposed = text.Trim().ToLowerInvariant().Replace('ł', 'l').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString();
    }
}
