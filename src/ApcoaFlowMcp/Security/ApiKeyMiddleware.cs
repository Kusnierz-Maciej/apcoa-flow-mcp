using System.Text;
using ApcoaFlowMcp.Security.OAuth;

namespace ApcoaFlowMcp.Security;

/// <summary>
/// Gates <c>/mcp</c> with either (a) a valid OAuth 2.1 Bearer access token issued by this server, or
/// (b) the legacy header scheme: <c>X-Api-Key</c> + <c>Authorization: Basic base64(email:password)</c>.
/// Both resolve to the per-request APCOA credentials stashed in <see cref="HttpContext.Items"/> for the
/// credential provider to read. There is no server-side default-credential fallback.
/// </summary>
public class ApiKeyMiddleware
{
    public const string EmailItemKey    = "apcoaflow-email";
    public const string PasswordItemKey = "apcoaflow-password";

    private readonly RequestDelegate _next;
    private readonly string? _apiKey;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next   = next;
        _apiKey = NullIfEmpty(configuration["Security:ApiKey"]);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    public async Task InvokeAsync(
        HttpContext context,
        CredentialTokenService tokens,
        ApiKeyAllowlist allowlist,
        ILogger<ApiKeyMiddleware> logger)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();

        // Path A: OAuth Bearer access token.
        if (authHeader is not null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader["Bearer ".Length..].Trim();
            var payload = tokens.TryUnprotect(token, TokenKind.Access);

            // Kill-switch: the embedded API-key hash must still be in the current allowlist.
            if (payload is null || !allowlist.IsHashAllowed(payload.ApiKeyHash))
            {
                logger.LogWarning("Rejected /mcp request: OAuth token invalid, expired, or its API key was revoked.");
                await Challenge(context);
                return;
            }

            context.Items[EmailItemKey]    = payload.Email;
            context.Items[PasswordItemKey] = payload.Password;
            await _next(context);
            return;
        }

        // Path B: legacy X-Api-Key + Basic.
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            var provided = context.Request.Headers["X-Api-Key"].FirstOrDefault();
            if (provided != _apiKey)
            {
                logger.LogWarning("Rejected /mcp request: missing or incorrect X-Api-Key.");
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }
        }

        var (email, password) = ParseBasicAuth(authHeader);
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            await Challenge(context,
                "APCOA Flow credentials are required. Authorize via OAuth, or send an " +
                "Authorization: Basic header with your APCOA email and password.");
            return;
        }

        context.Items[EmailItemKey]    = email;
        context.Items[PasswordItemKey] = password;
        await _next(context);
    }

    /// <summary>Emits 401 + a WWW-Authenticate pointing at the protected-resource metadata so clients re-auth.</summary>
    private static async Task Challenge(HttpContext context, string? message = null)
    {
        var resourceMetadata = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}" +
                               "/.well-known/oauth-protected-resource";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate =
            $"Bearer resource_metadata=\"{resourceMetadata}\"";
        if (message is not null)
            await context.Response.WriteAsync(message);
    }

    private static (string? email, string? password) ParseBasicAuth(string? header)
    {
        if (header is null || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return (null, null);

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
            var sep = decoded.IndexOf(':');
            if (sep < 0) return (null, null);
            return (decoded[..sep], decoded[(sep + 1)..]);
        }
        catch
        {
            return (null, null);
        }
    }
}
