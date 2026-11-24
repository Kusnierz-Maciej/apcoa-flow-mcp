namespace ApcoaFlowApiClient.Models.Auth;

public class LoginResponse
{
    public int UserId { get; set; }
    /// <summary>Bearer token — use this as the Authorization header value.</summary>
    public string AuthToken { get; set; } = string.Empty;
    /// <summary>TTL in milliseconds (observed: 7776000000 = 90 days).</summary>
    public long Ttl { get; set; }
    public string Created { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string? RefreshTokenStartDate { get; set; }
    public string? LanguageCode { get; set; }
    public bool FailedLogin { get; set; }
    public object? FailedLoginResponse { get; set; }
}
