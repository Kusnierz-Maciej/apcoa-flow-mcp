using ApcoaFlowApiClient.ParkingData;
using ApcoaFlowApiClient.ParkingData.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
            .AddHttpMessageHandler<ApcoaFlowAuthHandler>();

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
