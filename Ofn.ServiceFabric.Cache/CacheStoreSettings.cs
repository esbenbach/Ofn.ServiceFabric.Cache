namespace Ofn.ServiceFabric.Cache;

using System.ComponentModel.DataAnnotations;

/// <summary>Provides settings for the cache store implementation such as max size and default expiration settings.</summary>
public class CacheStoreSettings
{
    /// <summary>
    /// The maximum size of the cache in megabytes, defaults to 100 if not given.
    /// </summary>
    [Range(1, long.MaxValue, ErrorMessage = "MaxCacheSize must be greater than zero.")]
    public long MaxCacheSize { get; set; } = 100;

    /// <summary>
    /// The byte offset used for dynamically sizing the cache.
    /// </summary>
    [Range(0, int.MaxValue, ErrorMessage = "ByteSizeOffset must be non-negative.")]
    public int ByteSizeOffset { get; set; } = 250;

    /// <summary>
    /// The name of the cache service listener.
    /// </summary>
    [Required]
    public string ListenerName { get; set; } = "CacheStoreServiceListener";

    /// <summary>
    /// The cache pruning interval in seconds.
    /// </summary>
    /// <remarks>
    /// This indicates how often the service will scan for, and remove items that should be removed from the cache in case the cache is over its size limit.
    /// </remarks>
    [Range(1, int.MaxValue, ErrorMessage = "CachePruningInterval must be greater than zero.")]
    public int CachePruningInterval { get; set; } = 15;

    /// <summary>
    /// How often (in seconds) the service scans for and removes expired items regardless of cache size pressure.
    /// Defaults to 30 seconds.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "ExpirationScanInterval must be greater than zero.")]
    public int ExpirationScanInterval { get; set; } = 30;

    /// <summary>
    /// Maximum number of items inspected per expiry scan cycle.
    /// Limits the per-cycle cost when many items are present; any remaining items are handled in the next cycle.
    /// Defaults to 500.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "ExpirationScanBatchSize must be greater than zero.")]
    public int ExpirationScanBatchSize { get; set; } = 500;
}
