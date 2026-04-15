namespace Ofn.ServiceFabric.Cache.Client;

using System;

/// <summary>
/// Configuration options for <see cref="ServiceFabricDistributedCache"/>.
/// </summary>
public class ServiceFabricCacheOptions
{
    /// <summary>
    /// Explicit URI of the cache store service. When <c>null</c>, the client auto-discovers the service by scanning the cluster.
    /// </summary>
    public Uri? CacheStoreServiceUri { get; set; }

    /// <summary>
    /// SF remoting listener endpoint name on the cache store service. Defaults to <c>"CacheStoreServiceListener"</c> when <c>null</c>.
    /// </summary>
    public string? CacheStoreEndpointName { get; set; }

    /// <summary>
    /// Unique identifier for this logical cache; used as a key prefix to support multiple caches on a single store service.
    /// </summary>
    public Guid CacheStoreId { get; set; }

    /// <summary>
    /// The sliding expiration applied when a caller provides neither absolute nor sliding expiration.
    /// Set to <c>null</c> to require callers to always specify an expiration (an <see cref="InvalidOperationException"/>
    /// will be thrown if no expiration is provided and this is <c>null</c>).
    /// Defaults to 60 seconds.
    /// </summary>
    public TimeSpan? DefaultSlidingExpiration { get; set; } = TimeSpan.FromSeconds(60);
}