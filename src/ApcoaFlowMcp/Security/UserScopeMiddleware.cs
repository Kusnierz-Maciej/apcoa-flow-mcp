namespace ApcoaFlowMcp.Security;

/// <summary>
/// Opens a log scope for the duration of each /mcp request so every downstream log entry
/// (tools, session service, auth handler) automatically carries the authenticated user's email.
/// Must run AFTER <see cref="ApiKeyMiddleware"/>, which is what populates the email item.
/// </summary>
public sealed class UserScopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ILogger<UserScopeMiddleware> logger)
    {
        var email = context.Items[ApiKeyMiddleware.EmailItemKey] as string;
        if (string.IsNullOrWhiteSpace(email))
        {
            await next(context);
            return;
        }

        using (logger.BeginScope("{UserEmail}", email))
        {
            await next(context);
        }
    }
}
