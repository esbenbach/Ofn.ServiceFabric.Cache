namespace Ofn.ServiceFabric.Cache.Client;

using System;
using Microsoft.Extensions.Options;

public class ServiceFabricCacheOptions : IOptions<ServiceFabricCacheOptions>
{
    public ServiceFabricCacheOptions Value => this;

    public Uri CacheStoreServiceUri { get; set; }

    public string CacheStoreEndpointName { get; set; }

    public Guid CacheStoreId { get; set; }

    /// <summary>
    /// The sliding expiration applied when a caller provides neither absolute nor sliding expiration.
    /// Set to <c>null</c> to require callers to always specify an expiration (an <see cref="InvalidOperationException"/>
    /// will be thrown if no expiration is provided and this is <c>null</c>).
    /// Defaults to 60 seconds.
    /// </summary>
    public TimeSpan? DefaultSlidingExpiration { get; set; } = TimeSpan.FromSeconds(60);
}