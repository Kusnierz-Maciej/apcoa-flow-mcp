using ApcoaFlow.Sandbox.Dumping;
using ApcoaFlowApiClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ApcoaFlow.Sandbox;

/// <summary>
/// Builds the sandbox DI container: the real APCOA Flow API client (same handler chain the app
/// uses) plus the innermost <see cref="DumpingHttpHandler"/>, the parking data source, and the
/// MCP service layer so scenarios can exercise either level. Credentials come from user secrets
/// (ApcoaFlow:Email / ApcoaFlow:Password) or env vars — the same store the test projects use.
/// </summary>
public static class SandboxHost
{
    public static ServiceProvider Build(DumpRun run)
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets(typeof(SandboxHost).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();

        var options = new ApcoaFlowClientOptions
        {
            Email = config["ApcoaFlow:Email"]
                ?? throw new InvalidOperationException("ApcoaFlow:Email not set (user secret or env var)."),
            Password = config["ApcoaFlow:Password"]
                ?? throw new InvalidOperationException("ApcoaFlow:Password not set (user secret or env var)."),
        };

        var services = new ServiceCollection();

        // The dump run + handler. The handler is appended as the INNERMOST pipeline handler via the
        // optional hook, so it sees the fully decorated request and fires per physical send.
        services.AddSingleton(run);
        services.AddTransient<DumpingHttpHandler>();

        services.AddSingleton<IApcoaFlowCredentialProvider>(new StaticCredentialProvider(options));
        services.AddApcoaFlowApiClient(client => client.AddHttpMessageHandler<DumpingHttpHandler>());
        services.AddApcoaParkingDataSource(configure: o =>
            o.StorePath = Path.Combine(Path.GetTempPath(), "apcoa-sandbox-store"));

        return services.BuildServiceProvider();
    }

    private sealed class StaticCredentialProvider(ApcoaFlowClientOptions options) : IApcoaFlowCredentialProvider
    {
        public ApcoaFlowClientOptions GetCredentials() => options;
    }
}
