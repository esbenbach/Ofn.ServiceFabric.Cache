using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Ofn.ServiceFabric.Cache.Abstractions;
using System;

namespace Ofn.ServiceFabric.Cache.Client;

/// <summary>
/// Extension methods for registering the Service Fabric distributed cache with the DI container.
/// </summary>
public static class ServiceFabricCachingServicesExtensions
{
    /// <summary>
    /// Registers <see cref="ServiceFabricDistributedCache"/> as <see cref="IDistributedCache"/> along with its required dependencies.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add the services to.</param>
    /// <param name="setupAction">Optional action to configure <see cref="ServiceFabricCacheOptions"/>.</param>
    /// <returns>The <see cref="IServiceCollection"/> so that calls can be chained.</returns>
    public static IServiceCollection AddDistributedServiceFabricCache(this IServiceCollection services, Action<ServiceFabricCacheOptions>? setupAction = null)
    {
        if (services == null) throw new ArgumentNullException(nameof(services));

        services.AddOptions();
        services.Configure<ServiceFabricCacheOptions>(setupAction ?? (_ => { }));

        return services
            .AddSingleton<IDistributedCacheStoreLocator, DistributedCacheStoreLocator>()
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .AddSingleton<IDistributedCache, ServiceFabricDistributedCache>();
    }
}
