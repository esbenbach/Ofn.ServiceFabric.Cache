using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Ofn.ServiceFabric.Cache.Abstractions;
using System;

namespace Ofn.ServiceFabric.Cache.Client;

public static class ServiceFabricCachingServicesExtensions
{
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
