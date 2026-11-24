using System.Text;

namespace ApcoaFlowMcp.Security;

public class ApiKeyMiddleware
{
    public const string EmailItemKey    = "apcoaflow-email";
    public const string PasswordItemKey = "apcoaflow-password";

    private readonly RequestDelegate _next;
    private readonly string? _apiKey;
    private readonly string? _fallbackEmail;
    private readonly string? _fallbackPassword;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _apiKey           = NullIfEmpty(configuration["Security:ApiKey"]);
        _fallbackEmail    = NullIfEmpty(configuration["ApcoaFlow:Email"]);
        _fallbackPassword = NullIfEmpty(configuration["ApcoaFlow:Password"]);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            var provided = context.Request.Headers["X-Api-Key"].FirstOrDefault();

            if (provided != _apiKey)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }
        }

        var (email, password) = ParseBasicAuth(context.Request.Headers.Authorization.FirstOrDefault());

        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(password))
        {
            email    = _fallbackEmail;
            password = _fallbackPassword;
        }

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(
                "APCOA Flow credentials are required. Provide an Authorization: Basic header, " +
                "or configure ApcoaFlow:Email and ApcoaFlow:Password on the server.");
            return;
        }

        context.Items[EmailItemKey]    = email;
        context.Items[PasswordItemKey] = password;

        await _next(context);
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
