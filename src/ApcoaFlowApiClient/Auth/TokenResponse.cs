using System.Text.Json.Serialization;

namespace ApcoaFlowApiClient.Auth;

/// <summary>
/// <c>/connect/token</c> success: <c>{access_token, token_type: "Bearer", expires_in: 3600, refresh_token}</c>
/// (password and refresh grants alike; the refresh token rotates on every refresh). Observed 2026-11-27.
/// </summary>
public sealed class TokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
    [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
}

/// <summary>
/// <c>/connect/token</c> failure (OAuth error JSON). A wrong username/password is
/// <c>400 {"error":"invalid_grant","error_description":"The username/password couple is invalid."}</c>
/// (OpenIddict ID2024); an expired or reused refresh token is <c>invalid_grant</c> too.
/// </summary>
public sealed class TokenErrorResponse
{
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
}

/// <summary>An access token with its expiry and the refresh token that renews it.</summary>
public sealed record AccessTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt)
{
    /// <summary>Renew this long before expiry, so a call never leaves with a token that dies in flight.</summary>
    public static readonly TimeSpan RenewalMargin = TimeSpan.FromMinutes(2);

    public bool IsFresh(DateTimeOffset now) => now + RenewalMargin < ExpiresAt; // ExpiresAt may be MinValue (invalidated)

    public static AccessTokens From(TokenResponse response, DateTimeOffset now) =>
        new(response.AccessToken, response.RefreshToken, now.AddSeconds(response.ExpiresIn));
}
