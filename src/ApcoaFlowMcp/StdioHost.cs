using ApcoaFlowApiClient;
using ApcoaFlowMcp.Apcoa;
using ApcoaFlowMcp.Tools;

namespace ApcoaFlowMcp;

/// <summary>
/// Stdio mode: credentials come from ApcoaFlow__Email / ApcoaFlow__Password.
/// </summary>
public static class StdioHost
{
    public static async Task RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // stdout carries the MCP protocol, so all logs go to stderr.
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddTransient<IApcoaFlowCredentialProvider, ConfigurationCredentialProvider>();
        builder.Services.AddApcoaFlowApiClient();
        builder.Services.AddScoped<ApcoaFlowApiAccessor>();

        builder.Services
            .AddMcpServer(options => options.ServerInstructions = McpInstructions.Text)
            .WithStdioServerTransport()
            .WithTools<ApcoaFlowTools>();

        await builder.Build().RunAsync();
    }
}
