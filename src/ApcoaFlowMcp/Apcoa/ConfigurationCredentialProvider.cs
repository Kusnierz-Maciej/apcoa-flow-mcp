using ApcoaFlowApiClient;

namespace ApcoaFlowMcp.Apcoa;

/// <summary>Stdio mode: credentials come from configuration (ApcoaFlow__Email / ApcoaFlow__Password).</summary>
public class ConfigurationCredentialProvider(IConfiguration configuration) : IApcoaFlowCredentialProvider
{
    public ApcoaFlowClientOptions GetCredentials()
    {
        var email = configuration["ApcoaFlow:Email"];
        var password = configuration["ApcoaFlow:Password"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException(
                "APCOA Flow credentials are required. Set ApcoaFlow__Email and ApcoaFlow__Password.");

        return new ApcoaFlowClientOptions { Email = email, Password = password };
    }
}
