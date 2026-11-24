namespace ApcoaFlowApiClient.ParkingData.Index;

/// <summary>
/// Geohash encoding and neighbor traversal. Standard base-32 geohash:
/// bits alternate longitude/latitude, 5 bits per character.
/// </summary>
internal static class GeohashGrid
{
    private const string Base32 = "0123456789bcdefghjkmnpqrstuvwxyz";

    private static readonly Dictionary<char, int> CharIndex =
        Base32.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);

    /// <summary>
    /// Encodes a coordinate to a geohash of the given character length. Longitude and latitude
    /// bits interleave (longitude first), 5 bits per base-32 character.
    /// </summary>
    public static string Encode(double lat, double lon, int precision)
    {
        double minLat = -90, maxLat = 90, minLon = -180, maxLon = 180;
        var hash = new char[precision];
        bool isLon = true;
        int bits = 0, bitCount = 0, charIndex = 0;

        while (charIndex < precision)
        {
            if (isLon)
            {
                var mid = (minLon + maxLon) / 2;
                if (lon >= mid) { bits = (bits << 1) | 1; minLon = mid; }
                else { bits <<= 1; maxLon = mid; }
            }
            else
            {
                var mid = (minLat + maxLat) / 2;
                if (lat >= mid) { bits = (bits << 1) | 1; minLat = mid; }
                else { bits <<= 1; maxLat = mid; }
            }
            isLon = !isLon;

            if (++bitCount == 5)
            {
                hash[charIndex++] = Base32[bits];
                bits = 0;
                bitCount = 0;
            }
        }

        return new string(hash);
    }

    // Neighbor tables from the canonical geohash algorithm (Gustavo Niemeyer).
    private static readonly Dictionary<string, string[]> Neighbor = new()
    {
        ["n"] = ["p0r21436x8zb9dcf5h7kjnmqesgutwvy", "bc01fg45238967deuvhjyznpkmstqrwx"],
        ["s"] = ["14365h7k9dcfesgujnmqp0r2twvyx8zb", "238967debc01fg45kmstqrwxuvhjyznp"],
        ["e"] = ["bc01fg45238967deuvhjyznpkmstqrwx", "p0r21436x8zb9dcf5h7kjnmqesgutwvy"],
        ["w"] = ["238967debc01fg45kmstqrwxuvhjyznp", "14365h7k9dcfesgujnmqp0r2twvyx8zb"],
    };

    private static readonly Dictionary<string, string[]> Border = new()
    {
        ["n"] = ["prxz", "bcfguvyz"],
        ["s"] = ["028b", "0145hjnp"],
        ["e"] = ["bcfguvyz", "prxz"],
        ["w"] = ["0145hjnp", "028b"],
    };

    /// <summary>Returns the geohash of the cell adjacent to <paramref name="hash"/> in the given direction (n/s/e/w).</summary>
    public static string Adjacent(string hash, string direction)
    {
        hash = hash.ToLowerInvariant();
        var lastCh = hash[^1];
        var parent = hash[..^1];
        var type = hash.Length % 2; // 0 = even length

        if (Border[direction][type].IndexOf(lastCh) != -1 && parent.Length > 0)
            parent = Adjacent(parent, direction);

        return parent + Base32[Neighbor[direction][type].IndexOf(lastCh)];
    }

    /// <summary>Returns the 8 geohash cells surrounding <paramref name="hash"/> (same precision).</summary>
    public static IEnumerable<string> Neighbors(string hash)
    {
        var n = Adjacent(hash, "n");
        var s = Adjacent(hash, "s");
        yield return n;
        yield return s;
        yield return Adjacent(hash, "e");
        yield return Adjacent(hash, "w");
        yield return Adjacent(n, "e");
        yield return Adjacent(n, "w");
        yield return Adjacent(s, "e");
        yield return Adjacent(s, "w");
    }

    /// <summary>
    /// Returns the set of geohash cells (at <paramref name="precision"/>) covering the bounding box of a
    /// circle of <paramref name="radiusKm"/> around (lat, lon). Expands outward ring by ring until the
    /// covered cells exceed the bbox, so correctness holds for any radius.
    /// </summary>
    public static HashSet<string> CoveringCells(double lat, double lon, double radiusKm, int precision)
    {
        var latDelta = radiusKm / 111.0;
        var lonDelta = radiusKm / (111.0 * Math.Max(Math.Cos(lat * Math.PI / 180.0), 1e-6));

        var minLat = lat - latDelta;
        var maxLat = lat + latDelta;
        var minLon = lon - lonDelta;
        var maxLon = lon + lonDelta;

        // Seed with the geohashes of the bbox corners + center, then flood-fill neighbors
        // whose cells still intersect the bbox.
        var result = new HashSet<string>();
        var queue = new Queue<string>();
        var (cellLatSpan, cellLonSpan) = CellSpan(precision);

        foreach (var seed in new[]
                 {
                     Encode(lat, lon, precision),
                     Encode(minLat, minLon, precision),
                     Encode(minLat, maxLon, precision),
                     Encode(maxLat, minLon, precision),
                     Encode(maxLat, maxLon, precision),
                 })
        {
            if (result.Add(seed))
                queue.Enqueue(seed);
        }

        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            foreach (var neighbor in Neighbors(cell))
            {
                if (result.Contains(neighbor))
                    continue;

                var (cLat, cLon) = Decode(neighbor);
                // Include the neighbor if its center is within one cell-span of the bbox.
                if (cLat >= minLat - cellLatSpan && cLat <= maxLat + cellLatSpan &&
                    cLon >= minLon - cellLonSpan && cLon <= maxLon + cellLonSpan)
                {
                    result.Add(neighbor);
                    queue.Enqueue(neighbor);
                }
            }
        }

        return result;
    }

    /// <summary>Decodes a geohash to the center lat/lon of its cell.</summary>
    public static (double Lat, double Lon) Decode(string hash)
    {
        double minLat = -90, maxLat = 90, minLon = -180, maxLon = 180;
        bool isLon = true;

        foreach (var ch in hash.ToLowerInvariant())
        {
            var cd = CharIndex[ch];
            for (var mask = 16; mask != 0; mask >>= 1)
            {
                if (isLon)
                {
                    var mid = (minLon + maxLon) / 2;
                    if ((cd & mask) != 0) minLon = mid; else maxLon = mid;
                }
                else
                {
                    var mid = (minLat + maxLat) / 2;
                    if ((cd & mask) != 0) minLat = mid; else maxLat = mid;
                }
                isLon = !isLon;
            }
        }

        return ((minLat + maxLat) / 2, (minLon + maxLon) / 2);
    }

    /// <summary>Approximate lat/lon span (degrees) of a cell at the given precision.</summary>
    private static (double LatSpan, double LonSpan) CellSpan(int precision)
    {
        // Each character adds 5 bits: alternating lon/lat. Even chars split both equally;
        // bits allocated to lat vs lon differ by parity.
        var bits = precision * 5;
        var lonBits = (bits + 1) / 2;
        var latBits = bits / 2;
        var latSpan = 180.0 / Math.Pow(2, latBits);
        var lonSpan = 360.0 / Math.Pow(2, lonBits);
        return (latSpan, lonSpan);
    }
}
