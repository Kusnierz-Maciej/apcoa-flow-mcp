using System.Text.Json.Serialization;
using ApcoaFlowApiClient.Converters;

namespace ApcoaFlowApiClient.Models.Auth;

public class LoginResponse
{
    public int UserId { get; set; }

    /// <summary>Bearer token — use this as the Authorization header value.</summary>
    public string AuthToken { get; set; } = string.Empty;

    /// <summary>Token lifetime (API sends milliseconds; observed 90 days).</summary>
    [JsonPropertyName("ttl")]
    [JsonConverter(typeof(MillisecondsTimeSpanConverter))]
    public TimeSpan Ttl { get; set; }

    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? Created { get; set; }

    public string RefreshToken { get; set; } = string.Empty;

    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? RefreshTokenStartDate { get; set; }

    public string? LanguageCode { get; set; }
    public bool FailedLogin { get; set; }
    public object? FailedLoginResponse { get; set; }
}
