using System.Globalization;
using System.Text;
using ApcoaFlowApiClient.ParkingData.Store;

namespace ApcoaFlowApiClient.ParkingData.Index;

/// <summary>
/// Immutable in-memory spatial index over the slim parking dataset. Built once from a
/// <see cref="ParkingSnapshot"/>; queried for nearby parkings (optionally filtered by name),
/// by id, or by country.
/// </summary>
public sealed class ParkingSpatialIndex
{
    private readonly ParkingEntry[] _entries;
    private readonly Dictionary<string, List<int>> _buckets;
    private readonly Dictionary<int, int> _byId;
    private readonly Dictionary<string, List<int>> _byCountry;
    private readonly int _precision;

    /// <summary>When the underlying snapshot was built (UTC). Used by the data source to decide on refresh.</summary>
    public DateTimeOffset BuiltAtUtc { get; }

    /// <summary>Number of parkings in the index.</summary>
    public int Count => _entries.Length;

    /// <summary>
    /// Builds the index from a snapshot, bucketing every parking by its geohash at
    /// <paramref name="geohashPrecision"/> and indexing by id and country for O(1) lookups.
    /// </summary>
    public ParkingSpatialIndex(ParkingSnapshot snapshot, int geohashPrecision)
    {
        BuiltAtUtc = snapshot.BuiltAtUtc;
        _precision = geohashPrecision;
        _entries = snapshot.Entries.ToArray();

        _buckets = new Dictionary<string, List<int>>();
        _byId = new Dictionary<int, int>(_entries.Length);
        _byCountry = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < _entries.Length; i++)
        {
            var e = _entries[i];
            _byId[e.Id] = i;

            var cell = GeohashGrid.Encode(e.Latitude, e.Longitude, _precision);
            if (!_buckets.TryGetValue(cell, out var bucket))
                _buckets[cell] = bucket = [];
            bucket.Add(i);

            if (!string.IsNullOrEmpty(e.CountryCode))
            {
                if (!_byCountry.TryGetValue(e.CountryCode, out var cc))
                    _byCountry[e.CountryCode] = cc = [];
                cc.Add(i);
            }
        }
    }

    /// <summary>
    /// Returns parkings within <paramref name="radiusKm"/> of (lat, lon), ordered by distance ascending.
    /// If <paramref name="nameContains"/> is provided, only parkings whose <see cref="ParkingEntry.DisplayName"/>
    /// contains it (case- and diacritic-insensitive) are returned.
    /// </summary>
    public IReadOnlyList<(ParkingEntry Parking, double DistanceKm)> FindNearby(
        double latitude, double longitude, double radiusKm, string? nameContains = null)
    {
        var cells = GeohashGrid.CoveringCells(latitude, longitude, radiusKm, _precision);
        var needle = string.IsNullOrWhiteSpace(nameContains) ? null : Normalize(nameContains);

        var results = new List<(ParkingEntry, double)>();
        var seen = new HashSet<int>();

        foreach (var cell in cells)
        {
            if (!_buckets.TryGetValue(cell, out var bucket))
                continue;

            foreach (var idx in bucket)
            {
                if (!seen.Add(idx))
                    continue;

                var e = _entries[idx];
                var dist = HaversineKm(latitude, longitude, e.Latitude, e.Longitude);
                if (dist > radiusKm)
                    continue;

                if (needle is not null && !Normalize(e.DisplayName).Contains(needle, StringComparison.Ordinal))
                    continue;

                results.Add((e, dist));
            }
        }

        results.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        return results;
    }

    /// <summary>Returns the parking with the given id, or null if it is not in the index.</summary>
    public ParkingEntry? GetById(int parkingId) =>
        _byId.TryGetValue(parkingId, out var idx) ? _entries[idx] : null;

    /// <summary>Returns every parking for the given ISO country code (case-insensitive); empty if none.</summary>
    public IReadOnlyList<ParkingEntry> GetAllForCountry(string countryCode) =>
        _byCountry.TryGetValue(countryCode, out var indices)
            ? indices.Select(i => _entries[i]).ToList()
            : [];

    // -------------------------------------------------------------------------

    /// <summary>Lowercase + strip combining diacritics (so "Góry" matches "gory").</summary>
    private static string Normalize(string s)
    {
        var decomposed = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Great-circle distance in km between two coordinates.</summary>
    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
