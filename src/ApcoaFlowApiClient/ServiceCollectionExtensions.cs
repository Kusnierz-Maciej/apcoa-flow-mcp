using ApcoaFlowApiClient.ParkingData;
using ApcoaFlowApiClient.ParkingData.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Polly;
using Refit;

namespace ApcoaFlowApiClient;

public static class ServiceCollectionExtensions
{
    /// <summary>Named <see cref="HttpClient"/> used for the login call (no auth handler, same rate limit).</summary>
    public const string LoginClientName = "ApcoaFlowLogin";

    private const string RateLimitPipeline = "apcoa-rate-limit";

    /// <param name="services">The service collection.</param>
    /// <param name="configureClient">
    /// Optional extension point: lets a caller (e.g. the diagnostic sandbox) append extra handlers to both
    /// the API and the login pipelines. Handlers added here land AFTER the built-in ones — i.e. innermost,
    /// closest to the socket — so a dump handler sees the fully-decorated request and fires once per
    /// physical send, including the auth handler's retry re-send and the login call itself.
    /// </param>
    /// <param name="rateLimit">
    /// Optional <c>RateLimit</c> configuration section (<see cref="ApcoaFlowRateLimitOptions"/>); defaults apply
    /// when omitted. Values are validated at host start.
    /// </param>
    public static IServiceCollection AddApcoaFlowApiClient(
        this IServiceCollection services,
        Action<IHttpClientBuilder>? configureClient = null,
        IConfiguration? rateLimit = null)
    {
        var rateLimitOptions = services.AddOptions<ApcoaFlowRateLimitOptions>();
        if (rateLimit is not null)
            rateLimitOptions.Bind(rateLimit);
        rateLimitOptions.ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<ApcoaFlowRateLimitOptions>, ApcoaFlowRateLimitOptionsValidator>());

        services.AddSingleton<ApcoaFlowTokenStore>();
        services.TryAddSingleton<ApcoaFlowRateLimiter>();
        services.AddTransient<ApcoaFlowAuthHandler>();
        services.TryAddTransient<IApcoaFlowLoginValidator, ApcoaFlowLoginValidator>();

        var api = services
            .AddRefitClient<IApcoaFlowApi>()
            .ConfigureHttpClient(c => c.BaseAddress = new Uri(ApcoaFlowClientOptions.BaseUrl))
            .AddHttpMessageHandler<ApcoaFlowAuthHandler>();
        AddRateLimit(api);
        configureClient?.Invoke(api);

        // Login: pooled factory client (no socket exhaustion), the static app headers the backend requires,
        // and the same shared rate limit as the API client.
        var login = services.AddHttpClient(LoginClientName, c =>
        {
            c.BaseAddress = new Uri(ApcoaFlowClientOptions.BaseUrl);
            foreach (var (name, value) in ApcoaFlowClientOptions.AppHeaders)
                c.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        });
        AddRateLimit(login);
        configureClient?.Invoke(login);

        return services;
    }

    // Outermost handler, so a request waits for a token before auth/login work happens. Requests over the
    // limit queue (FIFO) rather than fail — we never hammer the backend and trip its 403 lockout.
    private static void AddRateLimit(IHttpClientBuilder builder) =>
        builder.AddResilienceHandler(RateLimitPipeline, (pipeline, context) =>
            pipeline.AddRateLimiter(context.ServiceProvider.GetRequiredService<ApcoaFlowRateLimiter>().Limiter));

    /// <summary>
    /// Registers <see cref="IApcoaParkingDataSource"/> (S3-backed, AES-128-GCM, disk-persisted, in-memory indexed).
    /// Requires <see cref="AddApcoaFlowApiClient"/> to be called first.
    /// Call <see cref="AddApcoaParkingRefreshService"/> as well in a long-running host to refresh proactively.
    /// </summary>
    public static IServiceCollection AddApcoaParkingDataSource(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<ParkingDataSourceOptions>? configure = null)
    {
        // Fail fast at host start on bad config: DataAnnotations cover single-field bounds, the
        // validator covers cross-field rules (positive ages, soft <= hard).
        var options = services.AddOptions<ParkingDataSourceOptions>();
        if (configuration is not null)
            options.Bind(configuration);
        if (configure is not null)
            options.Configure(configure);
        options.ValidateDataAnnotations().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<ParkingDataSourceOptions>, ParkingDataSourceOptionsValidator>());

        services.AddHttpClient(nameof(ApcoaS3ParkingDataSource));
        services.AddSingleton<IParkingDataStore, FileParkingDataStore>();
        services.AddSingleton<IApcoaParkingDataSource, ApcoaS3ParkingDataSource>();

        return services;
    }

    /// <summary>
    /// Registers the hosted service that proactively refreshes the parking index on a timer.
    /// Only call this in a long-running host (the web host), not in the stdio host.
    /// </summary>
    public static IServiceCollection AddApcoaParkingRefreshService(this IServiceCollection services)
    {
        services.AddHostedService<ParkingDataRefreshService>();
        return services;
    }
}
