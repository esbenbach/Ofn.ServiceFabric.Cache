namespace Ofn.ServiceFabric.Cache
{
    using System;
    using System.Collections.Generic;
    using System.Fabric;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Internal;
    using Microsoft.Extensions.Logging;
    using Microsoft.ServiceFabric.Data;
    using Microsoft.ServiceFabric.Data.Collections;
    using Microsoft.ServiceFabric.Services.Communication.Runtime;
    using Microsoft.ServiceFabric.Services.Remoting.V2.FabricTransport.Runtime;
    using Microsoft.ServiceFabric.Services.Runtime;
    using Ofn.ServiceFabric.Cache.Abstractions;

    public abstract class BaseCacheStoreService : StatefulService, ICacheStoreService
    {
        private const int BytesInMegabyte = 1048576; // 1024 * 1024

        internal const int ByteSizeOffset = 250;

        internal const string CacheStoreProperty = "CacheStore";

        internal const string CacheStorePropertyValue = "true";

        private readonly Uri serviceUri;

        private readonly IReliableStateManagerReplica2 _reliableStateManagerReplica;

        private readonly ILogger<ICacheStoreService> logger;

        private readonly ISystemClock _systemClock;

        private readonly CacheStoreSettings settings;

        private int partitionCount = 1;

        public BaseCacheStoreService(StatefulServiceContext context, CacheStoreSettings settings = null, ILogger<ICacheStoreService> logger = null)
            : base(context)
        {
            serviceUri = context.ServiceName;
            this.logger = logger;
            _systemClock = new SystemClock();
            this.settings = settings ?? new CacheStoreSettings();

            if (!StateManager.TryAddStateSerializer(new CachedItemSerializer()))
            {
                throw new InvalidOperationException("Failed to set CachedItem custom serializer");
            }

            if (!StateManager.TryAddStateSerializer(new CacheStoreMetadataSerializer()))
            {
                throw new InvalidOperationException("Failed to set CacheStoreMetadata custom serializer");
            }
        }

        public BaseCacheStoreService(StatefulServiceContext context, CacheStoreSettings settings, IReliableStateManagerReplica2 reliableStateManagerReplica, ISystemClock systemClock, ILogger<ICacheStoreService> logger = null)
            : base(context, reliableStateManagerReplica)
        {
            serviceUri = context.ServiceName;
            _reliableStateManagerReplica = reliableStateManagerReplica;
            this.logger = logger;
            _systemClock = systemClock;
            this.settings = settings;
        }

        protected async override Task OnOpenAsync(ReplicaOpenMode openMode, CancellationToken cancellationToken)
        {
            var client = new FabricClient();
            await client.PropertyManager.PutPropertyAsync(serviceUri, CacheStoreProperty, CacheStorePropertyValue);
            partitionCount = (await client.QueryManager.GetPartitionListAsync(serviceUri)).Count;
        }

        public async Task<byte[]> GetCachedItemAsync(string key)
        {
            var cacheStore = await StateManager.GetOrAddAsync<IReliableDictionary<string, CachedItem>>(CacheStoreConstants.CacheStoreName);

            var cacheResult = await RetryHelper.ExecuteWithRetry(StateManager, async (tx, cancellationToken, state) =>
            {
                logger.LogTrace("Get cached item called with key: {key} on partition id: {PartitionId}", key, Partition?.PartitionInfo.Id);
                return await cacheStore.TryGetValueAsync(tx, key);
            });

            if (cacheResult.HasValue)
            {
                var cachedItem = cacheResult.Value;
                var expireTime = cachedItem.AbsoluteExpiration;

                if (_systemClock.UtcNow < expireTime)
                {
                    if (cachedItem.SlidingExpiration != null)
                    { 
                        // Update the expiration time if sliding.
                        await SetCachedItemAsync(key, cachedItem.Value, cachedItem.SlidingExpiration, cachedItem.AbsoluteExpiration);
                    }

                    return cachedItem.Value;
                }
                else // Remove expired items on access - its a bit weird but it works
                {
                    await RemoveCachedItemAsync(key);
                }
            }

            return null;
        }
        
        public async Task SetCachedItemAsync(string key, byte[] value, TimeSpan? slidingExpiration, DateTimeOffset? absoluteExpiration)
        {
            if (slidingExpiration.HasValue)
            {
                var now = _systemClock.UtcNow;
                absoluteExpiration = now.AddMilliseconds(slidingExpiration.Value.TotalMilliseconds);
            }

            var cacheStore = await StateManager.GetOrAddAsync<IReliableDictionary<string, CachedItem>>(CacheStoreConstants.CacheStoreName);
            var cacheStoreMetadata = await StateManager.GetOrAddAsync<IReliableDictionary<string, CacheStoreMetadata>>(CacheStoreConstants.CacheStoreMetadataName);

            await RetryHelper.ExecuteWithRetry(StateManager, async (tx, cancellationToken, state) => 
            {
                logger.LogTrace("Set cached item called with key: {key} on partition id: {PartitionId}", key, Partition?.PartitionInfo.Id);
           
                Func<string, Task<ConditionalValue<CachedItem>>> getCacheItem = async (string cacheKey) => await cacheStore.TryGetValueAsync(tx, cacheKey, LockMode.Update);
                var linkedDictionaryHelper = new LinkedDictionaryHelper(getCacheItem, this.settings.ByteSizeOffset);

                var cacheStoreInfo = (await cacheStoreMetadata.TryGetValueAsync(tx, CacheStoreConstants.CacheStoreMetadataKey, LockMode.Update)).Value ?? new CacheStoreMetadata(0, null, null);
                var existingCacheItem = (await getCacheItem(key)).Value;
                var cachedItem = ApplyAbsoluteExpiration(existingCacheItem, absoluteExpiration) ?? new CachedItem(value, null, null, slidingExpiration, absoluteExpiration);

                // empty linked dictionary
                if (cacheStoreInfo.FirstCacheKey == null)
                {
                    var metadata = new CacheStoreMetadata(value.Length + ByteSizeOffset, key, key);
                    await cacheStoreMetadata.SetAsync(tx, CacheStoreConstants.CacheStoreMetadataKey, metadata);
                    await cacheStore.SetAsync(tx, key, cachedItem);
                }
                else
                {
                    var cacheMetadata = cacheStoreInfo;

                    // linked node already exists in dictionary
                    if (existingCacheItem != null)
                    {
                        var removeResult = await linkedDictionaryHelper.Remove(cacheStoreInfo, cachedItem);
                        cacheMetadata = removeResult.CacheStoreMetadata;
                        await ApplyChanges(tx, cacheStore, cacheStoreMetadata, removeResult);
                    }

                    // add to last
                    var addLastResult = await linkedDictionaryHelper.AddLast(cacheMetadata, key, cachedItem, value);
                    await ApplyChanges(tx, cacheStore, cacheStoreMetadata, addLastResult);
                }
            });
        }

        public async Task RemoveCachedItemAsync(string key)
        {
            var cacheStore = await StateManager.GetOrAddAsync<IReliableDictionary<string, CachedItem>>(CacheStoreConstants.CacheStoreName);
            var cacheStoreMetadata = await StateManager.GetOrAddAsync<IReliableDictionary<string, CacheStoreMetadata>>(CacheStoreConstants.CacheStoreMetadataName);

            await RetryHelper.ExecuteWithRetry(StateManager, async (tx, cancellationToken, state) =>
            {
                logger.LogTrace("Remove cached item called with key: {key} on partition id: {PartitionId}", key, Partition?.PartitionInfo.Id);

                var cacheResult = await cacheStore.TryRemoveAsync(tx, key);
                if (cacheResult.HasValue)
                {
                    Func<string, Task<ConditionalValue<CachedItem>>> getCacheItem = async (string cacheKey) => await cacheStore.TryGetValueAsync(tx, cacheKey, LockMode.Update);
                    var linkedDictionaryHelper = new LinkedDictionaryHelper(getCacheItem, ByteSizeOffset);

                    var cacheStoreInfo = (await cacheStoreMetadata.TryGetValueAsync(tx, CacheStoreConstants.CacheStoreMetadataKey, LockMode.Update)).Value ?? new CacheStoreMetadata(0, null, null);
                    var result = await linkedDictionaryHelper.Remove(cacheStoreInfo, cacheResult.Value);

                    await ApplyChanges(tx, cacheStore, cacheStoreMetadata, result);
                }
            });
        }

        protected override IEnumerable<ServiceReplicaListener> CreateServiceReplicaListeners()
        {
            yield return new ServiceReplicaListener(context => new FabricTransportServiceRemotingListener(context, this), this.settings.ListenerName);
        }

        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            var cacheStore = await StateManager.GetOrAddAsync<IReliableDictionary<string, CachedItem>>(CacheStoreConstants.CacheStoreName);
            var cacheStoreMetadata = await StateManager.GetOrAddAsync<IReliableDictionary<string, CacheStoreMetadata>>(CacheStoreConstants.CacheStoreMetadataName);

            while (true)
            {
                await RemoveLeastRecentlyUsedCacheItemWhenOverMaxSize(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(this.settings.CachePruningInterval), cancellationToken);
            }
        }

        /// <summary>
        /// Removes the least recently used cache items from the cache when over maximum size.
        /// </summary>
        /// <remarks>This is rather odd in that nothing is removed when it is expiring</remarks>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        protected async Task RemoveLeastRecentlyUsedCacheItemWhenOverMaxSize(CancellationToken cancellationToken)
        {
            var cacheStore = await StateManager.GetOrAddAsync<IReliableDictionary<string, CachedItem>>(CacheStoreConstants.CacheStoreName);
            var cacheStoreMetadata = await StateManager.GetOrAddAsync<IReliableDictionary<string, CacheStoreMetadata>>(CacheStoreConstants.CacheStoreMetadataName);
            bool continueRemovingItems = true;

            while (continueRemovingItems)
            {
                continueRemovingItems = false;
                cancellationToken.ThrowIfCancellationRequested();

                await RetryHelper.ExecuteWithRetry(StateManager, async (tx, cancelToken, state) =>
                {

                    var metadata = await cacheStoreMetadata.TryGetValueAsync(tx, CacheStoreConstants.CacheStoreMetadataKey, LockMode.Update);

                    if (metadata.HasValue)
                    {
                        logger.LogTrace("Size: {CurrentCacheSize}, MaxSize: {MaxCacheSize}", metadata.Value.Size, GetMaxSizeInBytes());

                        if (metadata.Value.Size > GetMaxSizeInBytes())
                        {
                            Func<string, Task<ConditionalValue<CachedItem>>> getCacheItem = async (string cacheKey) => await cacheStore.TryGetValueAsync(tx, cacheKey, LockMode.Update);
                            var linkedDictionaryHelper = new LinkedDictionaryHelper(getCacheItem, ByteSizeOffset);

                            var firstItemKey = metadata.Value.FirstCacheKey;
                            var firstCachedItem = (await getCacheItem(firstItemKey)).Value;

                            // Move item to last item if cached item is not expired
                            if (firstCachedItem.AbsoluteExpiration > _systemClock.UtcNow)
                            {
                                // remove cached item
                                var removeResult = await linkedDictionaryHelper.Remove(metadata.Value, firstCachedItem);
                                await ApplyChanges(tx, cacheStore, cacheStoreMetadata, removeResult);

                                // add to last
                                var addLastResult = await linkedDictionaryHelper.AddLast(removeResult.CacheStoreMetadata, firstItemKey, firstCachedItem, firstCachedItem.Value);
                                await ApplyChanges(tx, cacheStore, cacheStoreMetadata, addLastResult);

                                continueRemovingItems = addLastResult.CacheStoreMetadata.Size > GetMaxSizeInBytes();
                            }
                            else  // Remove 
                            {
                                logger.LogTrace("Auto Removing {key}", metadata.Value.FirstCacheKey);

                                var result = await linkedDictionaryHelper.Remove(metadata.Value, firstCachedItem);
                                await ApplyChanges(tx, cacheStore, cacheStoreMetadata, result);
                                await cacheStore.TryRemoveAsync(tx, metadata.Value.FirstCacheKey);

                                continueRemovingItems = result.CacheStoreMetadata.Size > GetMaxSizeInBytes();
                            }
                        }
                    }
                });

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }

        private long GetMaxSizeInBytes()
        {
            return (this.settings.MaxCacheSize * BytesInMegabyte) / partitionCount;
        }

        private async Task ApplyChanges(ITransaction tx, IReliableDictionary<string, CachedItem> cachedItemStore, IReliableDictionary<string, CacheStoreMetadata> cacheStoreMetadata, LinkedDictionaryItemsChanged linkedDictionaryItemsChanged)
        {
            foreach (var cacheItem in linkedDictionaryItemsChanged.CachedItemsToUpdate)
            {
                await cachedItemStore.SetAsync(tx, cacheItem.Key, cacheItem.Value);
            }
    
            await cacheStoreMetadata.SetAsync(tx, CacheStoreConstants.CacheStoreMetadataKey, linkedDictionaryItemsChanged.CacheStoreMetadata);
        }

        private CachedItem ApplyAbsoluteExpiration(CachedItem cachedItem, DateTimeOffset? absoluteExpiration)
        {
            if (cachedItem != null)
            {
                return new CachedItem(cachedItem.Value, cachedItem.BeforeCacheKey, cachedItem.AfterCacheKey, cachedItem.SlidingExpiration, absoluteExpiration);
            }

            return null;
        }
    }
}
