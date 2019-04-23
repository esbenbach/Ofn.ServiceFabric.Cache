namespace Ofn.ServiceFabric.Cache
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>Provides settings for the cache store implementation such as max size and default expiration settings.</summary>
    public class CacheStoreSettings
    {
        /// <summary>
        /// The maximum size of the cache in megabytes, defaults to 100 if not given.
        /// </summary>
        public long MaxCacheSize { get; set; } = 100;



        private const string CacheStoreProperty = "CacheStore";
        private const string CacheStorePropertyValue = "true";
        const int BytesInMegabyte = 1048576;
        const int ByteSizeOffset = 250;
        const string CacheStoreName = "CacheStore";
        const string CacheStoreMetadataName = "CacheStoreMetadata";
        const string CacheStoreMetadataKey = "CacheStoreMetadata";
        private const string ListenerName = "CacheStoreServiceListener";
        
    }
}
