namespace ApcoaFlowApiClient.Models.Auth;

public class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    /// <summary>ISO 3166-1 alpha-2, e.g. "PL". Required by the API.</summary>
    public string LocationCode { get; set; } = string.Empty;
    /// <summary>Always true in observed traffic.</summary>
    public bool FailedResponseRequired { get; set; } = true;
}
