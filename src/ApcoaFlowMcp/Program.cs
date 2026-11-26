using ApcoaFlowApiClient;
using ApcoaFlowMcp;
using ApcoaFlowMcp.Apcoa;
using ApcoaFlowMcp.Security;
using ApcoaFlowMcp.Security.OAuth;
using ApcoaFlowMcp.Tools;
using Microsoft.AspNetCore.HttpOverrides;
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

// Token encryption derives its key deterministically from Security:TokenSecret — nothing is
// persisted to disk, so tokens survive restarts/replicas and the secret alone controls validity.
var tokenSecret = builder.Configuration["Security:TokenSecret"];

// HTTP mode fails fast if the OAuth token secret is missing, the .env.example placeholder, or too
// short — a known or weak secret would let anyone forge session tokens carrying arbitrary APCOA
// credentials, and it is the sole key source for token encryption.
const string PlaceholderTokenSecret = "change-me-to-a-long-random-secret";
const int MinTokenSecretLength = 32;
if (string.IsNullOrWhiteSpace(tokenSecret) || tokenSecret == PlaceholderTokenSecret
    || tokenSecret.Length < MinTokenSecretLength)
{
    Console.Error.WriteLine(
        $"FATAL: Security__TokenSecret must be set to a long (>= {MinTokenSecretLength} chars), random, " +
        "stable secret before starting the HTTP server. It is the sole key for OAuth token encryption. " +
        "See docker/.env.example.");
    Environment.ExitCode = 1;
    return;
}

builder.Services.AddSingleton(new CredentialTokenService(tokenSecret));

builder.Services
    .AddMcpServer(options => options.ServerInstructions = McpInstructions.Text)
    .WithHttpTransport()
    .WithTools<ApcoaFlowTools>();

var app = builder.Build();

// Honor X-Forwarded-Proto/Host from the reverse proxy that terminates TLS, so Request.Scheme is
// "https" and the OAuth discovery metadata advertises https:// endpoints. The container is only
// reachable via the proxy, so the single forwarding hop is trusted (no known-proxy restriction).
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
};
forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaders);

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
