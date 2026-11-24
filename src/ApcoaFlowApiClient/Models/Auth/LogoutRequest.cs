namespace ApcoaFlowApiClient.Models.Auth;

public class LogoutRequest
{
    /// <summary>URL-encoded form field. Sent as application/x-www-form-urlencoded.</summary>
    public string RefreshToken { get; set; } = string.Empty;
}
