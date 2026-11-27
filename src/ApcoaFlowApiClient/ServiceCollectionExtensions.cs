using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Auth;
using ApcoaFlowApiClient.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Polly;
using Refit;

namespace ApcoaFlowApiClient;

public static class ServiceCollectionExtensions
{
    /// <summary>Named <see cref="HttpClient"/> for <c>/connect/token</c> (no auth handler, same rate limit).</summary>
    public const string AuthClientName = "ApcoaFlowAuth";

    private const string RateLimitPipeline = "apcoa-rate-limit";

    /// <summary>
    /// Registers <see cref="IApcoaFlowApi"/> with the auth handler, the token client/store, the sign-in validator and
    /// the shared rate limit. The caller registers an <see cref="IApcoaFlowCredentialProvider"/>.
    /// </summary>
    /// <param name="configureClient">
    /// Appends handlers to both pipelines (API and auth server), innermost, so e.g. the sandbox's dump handler sees each
    /// physical send, including logins, refreshes and the auth handler's retry.
    /// </param>
    /// <param name="rateLimit">The <c>RateLimit</c> section; defaults apply when null.</param>
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

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ApcoaFlowRateLimiter>();
        services.TryAddSingleton<TokenClient>();
        services.TryAddSingleton<TokenStore>();
        services.TryAddTransient<IApcoaFlowLoginValidator, ApcoaFlowLoginValidator>();
        services.AddTransient<ApcoaFlowAuthHandler>();

        var api = services
            .AddRefitClient<IApcoaFlowApi>()
            .ConfigureHttpClient(c => c.BaseAddress = new Uri(ApcoaFlowEndpoints.ApiBaseUrl))
            .AddHttpMessageHandler<ApcoaFlowAuthHandler>();
        AddRateLimit(api);
        configureClient?.Invoke(api);

        // The login is slow (~7 s server-side); keep the default 100 s timeout.
        var auth = services.AddHttpClient(AuthClientName, c => c.BaseAddress = new Uri(ApcoaFlowEndpoints.AuthBaseUrl));
        AddRateLimit(auth);
        configureClient?.Invoke(auth);

        return services;
    }

    // Outermost, so a request waits for a token before any auth work; over-limit requests queue instead of failing.
    private static void AddRateLimit(IHttpClientBuilder builder) =>
        builder.AddResilienceHandler(RateLimitPipeline, (pipeline, context) =>
            pipeline.AddRateLimiter(context.ServiceProvider.GetRequiredService<ApcoaFlowRateLimiter>().Limiter));
}
