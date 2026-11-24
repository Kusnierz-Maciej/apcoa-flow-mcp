using System.Threading.RateLimiting;
using ApcoaFlowApiClient.ParkingData;
using ApcoaFlowApiClient.ParkingData.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Refit;

namespace ApcoaFlowApiClient;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApcoaFlowApiClient(this IServiceCollection services)
    {
        services.AddSingleton<ApcoaFlowTokenStore>();
        services.AddTransient<ApcoaFlowAuthHandler>();

        services
            .AddRefitClient<IApcoaFlowApi>()
            .ConfigureHttpClient(c => c.BaseAddress = new Uri(ApcoaFlowClientOptions.BaseUrl))
            .AddHttpMessageHandler<ApcoaFlowAuthHandler>()
            // Client-side rate limit (outermost handler) so we never hammer the backend and trip its
            // 403 lockout. Token bucket: a short burst of up to 10 requests is allowed, then it settles
            // to a long-run average of 1 request/second as tokens refill one per second. Requests over
            // the limit queue (FIFO) rather than fail, so callers just slow down.
            .AddResilienceHandler("apcoa-rate-limit", builder =>
                builder.AddRateLimiter(new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 10,
                    TokensPerPeriod = 1,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = int.MaxValue,
                    AutoReplenishment = true,
                })));

        return services;
    }

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
        if (configuration is not null)
            services.Configure<ParkingDataSourceOptions>(configuration);
        if (configure is not null)
            services.Configure(configure);
        if (configuration is null && configure is null)
            services.Configure<ParkingDataSourceOptions>(_ => { });

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
