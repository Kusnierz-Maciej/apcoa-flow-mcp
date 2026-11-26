using ApcoaFlowApiClient;
using ApcoaFlowMcp;
using ApcoaFlowMcp.Apcoa;
using ApcoaFlowMcp.Security;
using ApcoaFlowMcp.Security.OAuth;
using ApcoaFlowMcp.Tools;
using Microsoft.AspNetCore.DataProtection;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

if (args.Contains("--stdio"))
{
    await StdioHost.RunAsync(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);

var resource = ResourceBuilder.CreateDefault()
    .AddService("apcoa-flow-mcp",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown");

if (builder.Configuration.GetValue<bool>("Telemetry:Enabled"))
{
    var otlpEndpoint = builder.Configuration["Telemetry:OtlpEndpoint"];

    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing
            .SetResourceBuilder(resource)
            .AddAspNetCoreInstrumentation()
            .AddOtlpExporter(o =>
            {
                if (otlpEndpoint is not null) o.Endpoint = new Uri(otlpEndpoint);
            }))
        .WithLogging(logging => logging
            .SetResourceBuilder(resource)
            .AddOtlpExporter(o =>
            {
                if (otlpEndpoint is not null) o.Endpoint = new Uri(otlpEndpoint);
            }));

    builder.Logging.AddOpenTelemetry(o =>
    {
        o.IncludeScopes = true;
        o.IncludeFormattedMessage = true;
    });
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<IApcoaFlowCredentialProvider, HttpContextCredentialProvider>();
builder.Services.AddApcoaFlowApiClient();
builder.Services.AddApcoaParkingDataSource(builder.Configuration.GetSection("ParkingData"));
builder.Services.AddApcoaParkingRefreshService();
builder.Services.Configure<ApcoaFlowToolsOptions>(builder.Configuration.GetSection("ApcoaFlow"));
builder.Services.AddScoped<ApcoaFlowSessionService>();

// OAuth authorization: API-key allowlist + encrypted stateless tokens carrying the APCOA credentials.
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection("Security"));
builder.Services.AddSingleton<ApiKeyAllowlist>();
builder.Services.AddSingleton<CredentialTokenService>();

// Data Protection seeds token encryption. Persist keys to a stable directory and derive from the
// configured TokenSecret so tokens survive restarts and are stable across replicas.
var dpBuilder = builder.Services.AddDataProtection().SetApplicationName("ApcoaFlowMcp");
var tokenSecret = builder.Configuration["Security:TokenSecret"];
if (!string.IsNullOrWhiteSpace(tokenSecret))
    dpBuilder.PersistKeysToFileSystem(new DirectoryInfo(DataProtectionKeyDir(tokenSecret)));

builder.Services
    .AddMcpServer(options => options.ServerInstructions = McpInstructions.Text)
    .WithHttpTransport()
    .WithTools<ApcoaFlowTools>();

var app = builder.Build();

app.UseMiddleware<RequestLoggingMiddleware>();

// OAuth authorization-server + protected-resource endpoints (public — no /mcp gate).
app.MapOAuthEndpoints();

app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/mcp"),
    branch =>
    {
        branch.UseMiddleware<CredentialFailureMiddleware>();
        branch.UseMiddleware<ApiKeyMiddleware>();
        branch.UseMiddleware<UserScopeMiddleware>();
    });

app.MapMcp("/mcp");

app.Logger.LogInformation("ApcoaFlowMcp v{Version} starting",
    typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown");

app.Run();

// Derives a per-secret key directory so changing TokenSecret rotates (invalidates) all tokens.
static string DataProtectionKeyDir(string secret)
{
    var hash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)))[..16];
    return Path.Combine(Path.GetTempPath(), "apcoaflow-mcp-dpkeys", hash);
}
