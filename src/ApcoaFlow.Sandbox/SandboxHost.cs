using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Auth;
using ApcoaFlow.Sandbox.Dumping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ApcoaFlow.Sandbox;

/// <summary>
/// Builds the sandbox DI container: the real API client (same handler chain the MCP uses) with the innermost
/// <see cref="DumpingHttpHandler"/> on both the API and the auth-server pipelines. Credentials come from user-secrets
/// (<c>ApcoaFlow:Email</c> / <c>ApcoaFlow:Password</c>, id <c>apcoa-flow-api-tests</c>, shared with the live
/// tests) or the env vars <c>ApcoaFlow__Email</c> / <c>ApcoaFlow__Password</c>.
/// </summary>
public static class SandboxHost
{
    public static ServiceProvider Build(DumpRun run)
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets(typeof(SandboxHost).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();

        var credentials = new ApcoaFlowCredentials(
            config["ApcoaFlow:Email"] ?? throw new InvalidOperationException("ApcoaFlow:Email not set (user secret or env var)."),
            config["ApcoaFlow:Password"] ?? throw new InvalidOperationException("ApcoaFlow:Password not set (user secret or env var)."));

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(new RunLogLoggerProvider(run)).SetMinimumLevel(LogLevel.Information)
            .AddFilter("System.Net.Http", LogLevel.Warning)); // the dump already shows every exchange
        services.AddSingleton(run);
        services.AddSingleton(credentials);
        services.AddTransient<DumpingHttpHandler>();
        services.AddSingleton<IApcoaFlowCredentialProvider>(new StaticCredentialProvider(credentials));
        services.AddApcoaFlowApiClient(client => client.AddHttpMessageHandler<DumpingHttpHandler>());
        return services.BuildServiceProvider();
    }

    private sealed class StaticCredentialProvider(ApcoaFlowCredentials credentials) : IApcoaFlowCredentialProvider
    {
        public ApcoaFlowCredentials GetCredentials() => credentials;
    }
}
