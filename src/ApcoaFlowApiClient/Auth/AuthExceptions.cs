namespace ApcoaFlowApiClient.Auth;

/// <summary>
/// APCOA rejected the stored credentials (password grant <c>invalid_grant</c>, or the API still refuses a freshly
/// issued token). The user must sign in again; the MCP host maps this to 401 + <c>WWW-Authenticate</c>.
/// </summary>
public sealed class ApcoaFlowCredentialsRejectedException(string? detail = null)
    : Exception("APCOA rejected the e-mail or password; sign in again." + (detail is null ? "" : $" ({detail})"));

/// <summary>The auth server failed for a reason other than bad credentials (5xx, timeout, odd response).</summary>
public sealed class ApcoaFlowAuthUnavailableException(string? detail = null)
    : Exception("APCOA sign-in is not available right now; try again shortly." + (detail is null ? "" : $" ({detail})"));

/// <summary>Finding the auth failures behind Refit's wrapping.</summary>
public static class ApcoaFlowAuthErrors
{
    /// <summary>
    /// Refit wraps an exception thrown inside the handler chain (the token store, the auth handler) in an
    /// <c>ApiRequestException</c>; this returns the <typeparamref name="T"/> anywhere in the inner chain, or null.
    /// </summary>
    public static T? Find<T>(Exception? ex) where T : Exception
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is T match)
                return match;
        return null;
    }
}
