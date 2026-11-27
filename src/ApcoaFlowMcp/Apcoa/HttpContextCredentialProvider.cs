using ApcoaFlowApiClient.Auth;
using ApcoaFlowMcp.Security;

namespace ApcoaFlowMcp.Apcoa;

/// <summary>HTTP host: the account of the current MCP request, resolved by <see cref="ApiKeyMiddleware"/>.</summary>
public class HttpContextCredentialProvider(IHttpContextAccessor accessor) : IApcoaFlowCredentialProvider
{
    public ApcoaFlowCredentials GetCredentials()
    {
        var context = accessor.HttpContext
            ?? throw new InvalidOperationException("No active HTTP context for the APCOA Flow request.");
        var email = context.Items[ApiKeyMiddleware.EmailItemKey] as string
            ?? throw new InvalidOperationException("APCOA Flow email not resolved for this request.");
        var password = context.Items[ApiKeyMiddleware.PasswordItemKey] as string
            ?? throw new InvalidOperationException("APCOA Flow password not resolved for this request.");
        return new ApcoaFlowCredentials(email, password);
    }
}
