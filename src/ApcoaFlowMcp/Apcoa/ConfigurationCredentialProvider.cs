using ApcoaFlowApiClient.Auth;
using Microsoft.Extensions.Options;

namespace ApcoaFlowMcp.Apcoa;

/// <summary>
/// Stdio mode: credentials come from configuration (ApcoaFlow__Email / ApcoaFlow__Password), validated at
/// startup by <see cref="StdioCredentialsOptionsValidator"/>.
/// </summary>
public class ConfigurationCredentialProvider(IOptions<StdioCredentialsOptions> options) : IApcoaFlowCredentialProvider
{
    public ApcoaFlowCredentials GetCredentials() => new(options.Value.Email!, options.Value.Password!);
}
