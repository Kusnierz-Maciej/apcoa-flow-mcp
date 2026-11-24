using ApcoaFlowApiClient;
using ApcoaFlowMcp;
using ApcoaFlowMcp.Apcoa;
using ApcoaFlowMcp.Security;
using ApcoaFlowMcp.Tools;

if (args.Contains("--stdio"))
{
    await StdioHost.RunAsync(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<IApcoaFlowCredentialProvider, HttpContextCredentialProvider>();
builder.Services.AddApcoaFlowApiClient();
builder.Services.AddScoped<ApcoaFlowApiAccessor>();

builder.Services
    .AddMcpServer(options => options.ServerInstructions = McpInstructions.Text)
    .WithHttpTransport()
    .WithTools<ApcoaFlowTools>();

var app = builder.Build();

app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/mcp"),
    branch => branch.UseMiddleware<ApiKeyMiddleware>());

app.MapMcp("/mcp");

app.Logger.LogInformation("ApcoaFlowMcp v{Version} starting",
    typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown");

app.Run();
