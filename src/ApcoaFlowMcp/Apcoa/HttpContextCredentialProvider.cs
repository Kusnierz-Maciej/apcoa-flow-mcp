using ApcoaFlowApiClient;
using ApcoaFlowMcp.Security;

namespace ApcoaFlowMcp.Apcoa;

public class HttpContextCredentialProvider(IHttpContextAccessor accessor) : IApcoaFlowCredentialProvider
{
    public ApcoaFlowClientOptions GetCredentials()
    {
        var context = accessor.HttpContext
            ?? throw new InvalidOperationException("No active HTTP context for the APCOA Flow request.");

        var email = context.Items[ApiKeyMiddleware.EmailItemKey] as string
            ?? throw new InvalidOperationException("APCOA Flow email not resolved for this request.");
        var password = context.Items[ApiKeyMiddleware.PasswordItemKey] as string
            ?? throw new InvalidOperationException("APCOA Flow password not resolved for this request.");

        return new ApcoaFlowClientOptions { Email = email, Password = password };
    }
}
