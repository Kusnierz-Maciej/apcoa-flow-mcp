using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ApcoaFlowApiClient.Tests;

internal static class ApiClientFactory
{
    private sealed class Marker;

    private static readonly Lazy<(IApcoaFlowApi Api, ApcoaFlowClientOptions Options)> _instance = new(Build);

    public static IApcoaFlowApi Api => _instance.Value.Api;
    public static ApcoaFlowClientOptions Options => _instance.Value.Options;

    private static (IApcoaFlowApi, ApcoaFlowClientOptions) Build()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<Marker>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var email    = config["ApcoaFlow:Email"]    ?? throw new InvalidOperationException("ApcoaFlow:Email not configured. Set via user-secrets or APCOAFLOW__EMAIL env var.");
        var password = config["ApcoaFlow:Password"] ?? throw new InvalidOperationException("ApcoaFlow:Password not configured. Set via user-secrets or APCOAFLOW__PASSWORD env var.");

        var options = new ApcoaFlowClientOptions { Email = email, Password = password };
        var services = new ServiceCollection();
        services.AddSingleton<IApcoaFlowCredentialProvider>(new StaticCredentialProvider(options));
        services.AddApcoaFlowApiClient();
        return (services.BuildServiceProvider().GetRequiredService<IApcoaFlowApi>(), options);
    }

    private sealed class StaticCredentialProvider(ApcoaFlowClientOptions options) : IApcoaFlowCredentialProvider
    {
        public ApcoaFlowClientOptions GetCredentials() => options;
    }
}
