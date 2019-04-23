namespace Ofn.ServiceFabric.Cache
{
    /// <summary>Provides settings for the cache store implementation such as max size and default expiration settings.</summary>
    public class CacheStoreSettings
    {
        /// <summary>
        /// The maximum size of the cache in megabytes, defaults to 100 if not given.
        /// </summary>
        public long MaxCacheSize { get; set; } = 100;

        /// <summary>
        /// The byte offset used for dynamically sizing the cache.
        /// </summary>
        public int ByteSizeOffset { get; set; } = 250;

        /// <summary>
        /// The name of the cache service listener.
        /// </summary>
        public string ListenerName { get; set; } = "CacheStoreServiceListener";

        /// <summary>
        /// The cache pruning interval in seconds.
        /// </summary>
        /// <remarks>
        /// This indicates how often the service will scan for, and remove items that should be removed from the cache in case the cache is over its size limit.
        /// </remarks>
        public int CachePruningInterval { get; set; } = 15;
    }
}
