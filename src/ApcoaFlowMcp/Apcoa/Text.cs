using System.Globalization;
using ApcoaFlowApiClient;

namespace ApcoaFlowMcp.Apcoa;

/// <summary>How tool results write durations, times and distances.</summary>
public static class Text
{
    public static string Duration(int minutes) => minutes switch
    {
        < 60 => $"{minutes} min",
        _ when minutes % (24 * 60) == 0 => minutes == 24 * 60 ? "1 day" : $"{minutes / (24 * 60)} days",
        _ when minutes % 60 == 0 => minutes == 60 ? "1 h" : $"{minutes / 60} h",
        _ => $"{minutes / 60} h {minutes % 60} min",
    };

    /// <summary>Warsaw time; "HH:mm" today, else with the date.</summary>
    public static string LocalTime(DateTimeOffset utc, TimeProvider time) => LocalTime(utc, time, ApcoaFlowEndpoints.AppTimeZone);

    /// <summary>The time in <paramref name="zone"/> (a parking's, see <see cref="ParkingTime"/>); "HH:mm" today, else with the date.</summary>
    public static string LocalTime(DateTimeOffset utc, TimeProvider time, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        var today = TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).Date;
        return local.Date == today
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public static string Meters(double m) =>
        m < 1000 ? FormattableString.Invariant($"{m:0} m") : FormattableString.Invariant($"{m / 1000:0.0} km");
}
