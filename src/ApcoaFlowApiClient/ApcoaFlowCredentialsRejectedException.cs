namespace ApcoaFlowApiClient;

/// <summary>
/// Thrown by <see cref="ApcoaFlowAuthHandler"/> when APCOA rejects the stored credentials even after a
/// fresh re-login — i.e. the email/password are no longer valid (typically the user changed their APCOA
/// password after authorizing). The MCP layer maps this to an HTTP 401 + <c>WWW-Authenticate</c> so the
/// client re-runs the OAuth flow, and tools surface a clear "re-authenticate" message. This is distinct
/// from a transient API error, which is NOT represented by this exception.
/// </summary>
public sealed class ApcoaFlowCredentialsRejectedException(string? detail = null)
    : Exception("APCOA rejected the stored credentials; re-authentication is required." +
                (detail is null ? "" : $" ({detail})"));
