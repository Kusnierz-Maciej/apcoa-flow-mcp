using ApcoaFlowApiClient;
using ApcoaFlowMcp;
using ApcoaFlowMcp.Apcoa;
using ApcoaFlowMcp.Security;
using ApcoaFlowMcp.Security.OAuth;
using ApcoaFlowMcp.Tools;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
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

// Telemetry is decided at build time (it changes what gets registered), so read it eagerly here;
// the same section is also registered as validated options below, so a bad endpoint fails at start.
var telemetrySection = builder.Configuration.GetSection("Telemetry");
var telemetry = telemetrySection.Get<TelemetryOptions>() ?? new TelemetryOptions();
builder.Services.AddOptions<TelemetryOptions>().Bind(telemetrySection).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<TelemetryOptions>, TelemetryOptionsValidator>();

if (telemetry.Enabled)
{
    // Only applied when valid — an invalid endpoint is reported by TelemetryOptionsValidator instead.
    TelemetryOptionsValidator.IsValidEndpoint(telemetry.OtlpEndpoint, out var otlpEndpoint);

    // Keep query strings in HttpClient span URLs (url.full) — .NET redacts them by default. They carry
    // ids, the plate and tariff parameters that make APCOA calls debuggable. No secrets travel in query
    // strings (credentials are in the login body, tokens in headers). Process-wide; telemetry-only.
    AppContext.SetSwitch("System.Net.Http.DisableUriRedaction", true);

    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing
            .SetResourceBuilder(resource)
            .AddAspNetCoreInstrumentation(o =>
            {
                // Attach unhandled exceptions (type, message, stack trace) to the request span.
                o.RecordException = true;
                o.Filter = ctx => !TelemetryNoise.IsNoise(ctx.Request.Path);
            })
            // MCP SDK spans: one per JSON-RPC request, e.g. "tools/call find_parking" (tag gen_ai.tool.name).
            .AddSource("Experimental.ModelContextProtocol")
            // .NET built-in HttpClient spans: outbound APCOA API + S3 calls.
            .AddSource("System.Net.Http")
            // App spans: parking index load/build/refresh, APCOA login; business tags on tool spans.
            .AddSource(ApcoaTelemetry.SourceName)
            .AddOtlpExporter(o =>
            {
                if (otlpEndpoint is not null) o.Endpoint = otlpEndpoint;
            }))
        .WithLogging(logging => logging
            .SetResourceBuilder(resource)
            .AddOtlpExporter(o =>
            {
                if (otlpEndpoint is not null) o.Endpoint = otlpEndpoint;
            }));

    builder.Logging.AddOpenTelemetry(o =>
    {
        o.IncludeScopes = true;
        o.IncludeFormattedMessage = true;
    });
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<IApcoaFlowCredentialProvider, HttpContextCredentialProvider>();
builder.Services.AddApcoaFlowApiClient(
    rateLimit: builder.Configuration.GetSection(ApcoaFlowRateLimitOptions.Section));
builder.Services.AddApcoaParkingDataSource(builder.Configuration.GetSection("ParkingData"));
builder.Services.AddApcoaParkingRefreshService();
builder.Services.AddOptions<ApcoaFlowToolsOptions>()
    .Bind(builder.Configuration.GetSection("ApcoaFlow"))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddScoped<ApcoaFlowSessionService>();

// OAuth authorization: API-key allowlist + encrypted stateless tokens carrying the APCOA credentials.
// SecurityOptionsValidator fails startup on a missing/placeholder/short TokenSecret or an empty
// allowlist while RequireMcpApiKey is on (HTTP host only — stdio has no OAuth).
builder.Services.AddOptions<SecurityOptions>()
    .Bind(builder.Configuration.GetSection("Security"))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<SecurityOptions>, SecurityOptionsValidator>();
builder.Services.AddSingleton<ApiKeyAllowlist>();
builder.Services.AddOptions<OAuthOptions>()
    .Bind(builder.Configuration.GetSection(OAuthOptions.Section))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<OAuthOptions>, OAuthOptionsValidator>();

// Token encryption derives its key deterministically from Security:TokenSecret — nothing is
// persisted to disk, so tokens survive restarts/replicas and the secret alone controls validity.
builder.Services.AddSingleton(sp =>
    new CredentialTokenService(sp.GetRequiredService<IOptions<SecurityOptions>>().Value.TokenSecret!));

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = McpServerIdentity.Create();
        options.ServerInstructions = McpInstructions.Text;
    })
    .WithHttpTransport()
    .WithApcoaFlowTools();

var app = builder.Build();

// Fail fast on invalid configuration with a readable message (and exit code 1) before anything is
// served. Host start would also run these ValidateOnStart checks, but it logs a stack trace first.
if (!StartupValidation.TryValidate(app.Services))
    return;

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

// Favicon / touch icons (wwwroot) for the OAuth sign-in page and for clients that look up a
// connector's icon from the server origin. Public, like the OAuth endpoints.
app.UseStaticFiles();

app.UseMiddleware<RequestLoggingMiddleware>();

// OAuth authorization-server + protected-resource endpoints (public — no /mcp gate).
app.MapOAuthEndpoints();

app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/mcp"),
    branch =>
    {
        branch.UseMiddleware<ApiKeyMiddleware>();
        branch.UseMiddleware<UserScopeMiddleware>();
    });

app.MapMcp("/mcp");

app.Logger.LogInformation("ApcoaFlowMcp v{Version} starting",
    typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown");

app.Run();
