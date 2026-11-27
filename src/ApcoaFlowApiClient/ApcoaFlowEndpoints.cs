namespace ApcoaFlowApiClient;

/// <summary>
/// Fixed addresses and app identity of the APCOA Flow API, as the APCOA Flow app 26.12.1 (web and mobile) uses them.
/// Observed 2026-11-27; see docs/API-NOTES.md.
/// </summary>
public static class ApcoaFlowEndpoints
{
    /// <summary>The REST API: every <c>/api/v1/…</c> call.</summary>
    public const string ApiBaseUrl = "https://api.nucleus.apcoaflow.com";

    /// <summary>The OpenIddict OAuth server; <c>/connect/token</c> serves the password and refresh grants.</summary>
    public const string AuthBaseUrl = "https://auth.prd.nucleus.valuespaces.io";

    /// <summary>The app's public OAuth client (no secret).</summary>
    public const string ClientId = "flownextapp.prd.nucleus.valuespaces.io";

    /// <summary><c>offline_access</c> makes the token endpoint return a refresh token.</summary>
    public const string Scope = "offline_access";

    /// <summary>
    /// Headers the web app sends on every API call. <c>x-user-agent</c> names the app build; the server's
    /// behaviour for other values is unknown, so we send the app's own.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> AppHeaders = new Dictionary<string, string>
    {
        ["x-user-agent"] = "Flow 26.12.1 (Web)",
        ["Accept-Language"] = "en",
    };

    /// <summary>The app sends the device's UTC offset in minutes (<c>60</c> in Poland in winter).</summary>
    public const string TimezoneOffsetHeader = "X-Timezone-Offset";

    /// <summary>Time zone used for <see cref="TimezoneOffsetHeader"/>: the MCP serves Polish parkings.</summary>
    public static readonly TimeZoneInfo AppTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    /// <summary>The current <see cref="TimezoneOffsetHeader"/> value.</summary>
    public static string TimezoneOffsetMinutes(DateTimeOffset now) =>
        ((int)AppTimeZone.GetUtcOffset(now).TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
