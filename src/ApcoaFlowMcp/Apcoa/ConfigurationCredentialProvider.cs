using ApcoaFlowApiClient;
using Microsoft.Extensions.Options;

namespace ApcoaFlowMcp.Apcoa;

/// <summary>
/// Stdio mode: credentials come from configuration (ApcoaFlow__Email / ApcoaFlow__Password), validated at
/// startup by <see cref="StdioCredentialsOptionsValidator"/>.
/// </summary>
public class ConfigurationCredentialProvider(IOptions<StdioCredentialsOptions> options) : IApcoaFlowCredentialProvider
{
    public ApcoaFlowClientOptions GetCredentials() => new()
    {
        Email = options.Value.Email!,
        Password = options.Value.Password!,
    };
}
