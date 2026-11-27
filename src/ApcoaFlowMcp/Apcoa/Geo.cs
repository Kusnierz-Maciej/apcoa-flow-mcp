using ApcoaFlowApiClient.Models;

namespace ApcoaFlowMcp.Apcoa;

/// <summary>
/// Distances for "nearest parking": metres from a position to a parking's outline (0 inside it, else to its nearest
/// edge) when it has one, else to its pin. Parkings are small, so a local flat projection around the position is
/// accurate to well under a metre.
/// </summary>
public static class Geo
{
    private const double EarthRadiusMeters = 6_371_000;

    /// <summary>Great-circle distance in metres.</summary>
    public static double Meters(double lat1, double lon1, double lat2, double lon2)
    {
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>How far a position is from a parking, and whether it's inside the parking's outline.</summary>
    public readonly record struct Distance(double Meters, bool Inside, bool FromOutline);

    /// <summary>
    /// Distance from (<paramref name="lat"/>, <paramref name="lon"/>) to the parking: to its outline when it has one
    /// with at least three points, else to its pin; null when it has neither.
    /// </summary>
    public static Distance? To(double lat, double lon, Coordinates? pin, GeoBorder? outline)
    {
        var ring = outline?.Coordinates;
        if (ring is { Count: >= 3 })
        {
            var points = ring.Select(c => Project(lat, lon, c)).ToList();
            if (Contains(points))
                return new Distance(0, Inside: true, FromOutline: true);
            var nearest = double.MaxValue;
            for (var i = 0; i < points.Count; i++)
                nearest = Math.Min(nearest, ToSegment(points[i], points[(i + 1) % points.Count]));
            return new Distance(nearest, Inside: false, FromOutline: true);
        }
        return pin is null ? null : new Distance(Meters(lat, lon, pin.Latitude, pin.Longitude), Inside: false, FromOutline: false);
    }

    /// <summary>A bounding box of ±<paramref name="radiusMeters"/> around a position.</summary>
    public static (double SwLat, double SwLon, double NeLat, double NeLon) Box(double lat, double lon, double radiusMeters)
    {
        var dLat = radiusMeters / EarthRadiusMeters * 180 / Math.PI;
        var dLon = dLat / Math.Max(0.01, Math.Cos(lat * Math.PI / 180));
        return (lat - dLat, lon - dLon, lat + dLat, lon + dLon);
    }

    // Local flat (equirectangular) coordinates in metres, with the position at the origin.
    private static (double X, double Y) Project(double originLat, double originLon, Coordinates c) =>
        ((c.Longitude - originLon) * Math.PI / 180 * EarthRadiusMeters * Math.Cos(originLat * Math.PI / 180),
         (c.Latitude - originLat) * Math.PI / 180 * EarthRadiusMeters);

    // Ray casting from the origin (the position) along +X.
    private static bool Contains(List<(double X, double Y)> ring)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var (xi, yi) = ring[i];
            var (xj, yj) = ring[j];
            if ((yi > 0) != (yj > 0) && 0 < (xj - xi) * (0 - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    // Distance from the origin to the segment a–b.
    private static double ToSegment((double X, double Y) a, (double X, double Y) b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(-(a.X * dx + a.Y * dy) / lengthSquared, 0, 1);
        var (px, py) = (a.X + t * dx, a.Y + t * dy);
        return Math.Sqrt(px * px + py * py);
    }
}
