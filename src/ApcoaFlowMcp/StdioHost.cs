using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Auth;
using ApcoaFlowApiClient.Http;
using ApcoaFlowMcp.Apcoa;
using ApcoaFlowMcp.Prompts;
using ApcoaFlowMcp.Tools;
using Microsoft.Extensions.Options;

namespace ApcoaFlowMcp;

/// <summary>
/// Stdio mode: credentials come from ApcoaFlow__Email / ApcoaFlow__Password (required at startup).
/// </summary>
public static class StdioHost
{
    public static async Task RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // stdout carries the MCP protocol, so all logs go to stderr.
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddOptions<StdioCredentialsOptions>()
            .Bind(builder.Configuration.GetSection(StdioCredentialsOptions.Section))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<StdioCredentialsOptions>, StdioCredentialsOptionsValidator>();
        builder.Services.AddTransient<IApcoaFlowCredentialProvider, ConfigurationCredentialProvider>();
        builder.Services.AddApcoaFlowApiClient(
            rateLimit: builder.Configuration.GetSection(ApcoaFlowRateLimitOptions.Section));
        builder.Services.AddScoped<ParkingFinder>();
        builder.Services.AddScoped<Pricing>();
        builder.Services.AddScoped<Sessions>();
        builder.Services.AddOptions<ApcoaFlowToolsOptions>()
            .Bind(builder.Configuration.GetSection("ApcoaFlow"))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = McpServerIdentity.Create();
                options.ServerInstructions = McpInstructions.Text;
            })
            .WithStdioServerTransport()
            .WithApcoaFlowTools()
            .WithApcoaFlowPrompts(builder.Configuration)
                    .WithRequestFilters(filters => filters.AddCallToolFilter(ClientContextFilter.Create));

        var host = builder.Build();
        if (!StartupValidation.TryValidate(host.Services))
            return;

        await host.RunAsync();
    }
}
