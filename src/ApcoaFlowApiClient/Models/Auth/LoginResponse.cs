using System.Text.Json.Serialization;
using ApcoaFlowApiClient.Converters;

namespace ApcoaFlowApiClient.Models.Auth;

public class LoginResponse
{
    public int? UserId { get; set; }

    /// <summary>Bearer token — use this as the Authorization header value. Null on a failed login.</summary>
    public string? AuthToken { get; set; }

    /// <summary>Token lifetime (API sends milliseconds; observed 90 days). Null on a failed login.</summary>
    [JsonPropertyName("ttl")]
    [JsonConverter(typeof(NullableMillisecondsTimeSpanConverter))]
    public TimeSpan? Ttl { get; set; }

    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? Created { get; set; }

    public string? RefreshToken { get; set; }

    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? RefreshTokenStartDate { get; set; }

    public string? LanguageCode { get; set; }
    public bool FailedLogin { get; set; }
    public object? FailedLoginResponse { get; set; }
}
