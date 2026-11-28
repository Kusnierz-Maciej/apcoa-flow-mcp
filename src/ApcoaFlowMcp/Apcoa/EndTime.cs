using System.Globalization;
using ApcoaFlowApiClient;

namespace ApcoaFlowMcp.Apcoa;

/// <summary>
/// "Until when" for a stay, the way people say it: a clock time in the parking's time zone (<see cref="ParkingTime"/>),
/// or a full date and time. A bare clock time means its next occurrence ("until 8:00" at 23:00 is tomorrow morning).
/// </summary>
public static class EndTime
{
    private static readonly string[] ClockFormats = ["H:mm", "HH:mm", "H.mm", "HH.mm"];
    private static readonly string[] LocalFormats =
        ["yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss"];

    /// <summary>Description shared by the tools' <c>until</c> parameter.</summary>
    public const string ParameterDescription =
        "Instead of durationMinutes: when the stay should end, in the parking's local time — \"18:00\" (the next 18:00, so " +
        "\"8:00\" late in the evening means tomorrow morning) or a date and time \"2026-12-05 18:00\". Give one of the two, not both.";

    /// <summary>Parses <paramref name="text"/> into a UTC end strictly after now, or says why it can't.</summary>
    public static bool TryParse(string text, TimeProvider time, out DateTimeOffset endUtc, out string error) =>
        TryParse(text, time, ApcoaFlowEndpoints.AppTimeZone, out endUtc, out error);

    /// <summary>Parses <paramref name="text"/> in <paramref name="zone"/> (the parking's) into a UTC end strictly after now.</summary>
    public static bool TryParse(string text, TimeProvider time, TimeZoneInfo zone, out DateTimeOffset endUtc, out string error)
    {
        endUtc = default;
        error = "";
        var s = text.Trim();
        var now = time.GetUtcNow();

        if (TimeOnly.TryParseExact(s, ClockFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var clock))
        {
            var today = TimeZoneInfo.ConvertTime(now, zone).Date;
            endUtc = ToUtc(today.Add(clock.ToTimeSpan()), zone);
            if (endUtc <= now) endUtc = ToUtc(today.AddDays(1).Add(clock.ToTimeSpan()), zone);
            return true;
        }
        if (DateTime.TryParseExact(s, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            endUtc = ToUtc(local, zone);
        else if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset) && HasOffset(s))
            endUtc = withOffset.ToUniversalTime();
        else
        {
            error = $"\"{text}\" isn't a time. Use a clock time like \"18:00\" or a date and time like \"2026-12-05 18:00\" (the parking's local time).";
            return false;
        }
        if (endUtc <= now)
        {
            error = $"{Text.LocalTime(endUtc, time, zone)} is already past.";
            return false;
        }
        return true;
    }

    /// <summary>Whole minutes from <paramref name="from"/> to <paramref name="end"/>, rounded up so the stay reaches the end.</summary>
    public static int MinutesFrom(DateTimeOffset from, DateTimeOffset end) => (int)Math.Ceiling((end - from).TotalMinutes);

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone), TimeSpan.Zero);

    private static bool HasOffset(string s) => s.EndsWith('Z') || s.EndsWith('z') || s.LastIndexOfAny(['+', '-']) > 10;
}
