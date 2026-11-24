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
}
