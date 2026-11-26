using ApcoaFlowApiClient;

namespace ApcoaFlowMcp.Security;

/// <summary>
/// Catches <see cref="ApcoaFlowCredentialsRejectedException"/> bubbling out of a tool call (APCOA rejected
/// the stored credentials — typically the user changed their password after authorizing) and turns it into
/// an HTTP 401 + <c>WWW-Authenticate</c> so a spec-compliant client restarts the OAuth flow, plus a clear
/// plain-language body. Centralizes the "re-authenticate" response so no tool needs its own try/catch.
/// </summary>
public sealed class CredentialFailureMiddleware(RequestDelegate next, ILogger<CredentialFailureMiddleware> logger)
{
    public const string ReauthMessage =
        "APCOA login failed — your password may have changed. Re-authenticate this connection in your " +
        "AI client's settings (disconnect and reconnect the APCOA MCP server).";

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ApcoaFlowCredentialsRejectedException ex)
        {
            logger.LogError(ex, "APCOA rejected the stored credentials; signalling re-authentication to the client.");

            if (context.Response.HasStarted)
                throw; // can't rewrite a response already in flight

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            var resourceMetadata = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}" +
                                   "/.well-known/oauth-protected-resource";
            context.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{resourceMetadata}\"";
            await context.Response.WriteAsync(ReauthMessage);
        }
    }
}
