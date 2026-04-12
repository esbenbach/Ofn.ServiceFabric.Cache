namespace Ofn.ServiceFabric.Cache;

using System.Fabric;
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

    internal const string CacheStoreProperty = "CacheStore";

    internal const string CacheStorePropertyValue = "true";

    private readonly Uri serviceUri;

    private readonly ILogger<ICacheStoreService>? logger;

    private readonly TimeProvider timeProvider;

    private readonly CacheStoreSettings settings;

    private int partitionCount = 1;

    protected IReliableDictionary<string, CachedItem>? _cacheStore;
    protected IReliableDictionary<string, CacheStoreMetadata>? _cacheStoreMetadata;

    public BaseCacheStoreService(StatefulServiceContext context, CacheStoreSettings? settings = null, ILogger<ICacheStoreService>? logger = null)
        : base(context)
    {
        this.serviceUri = context.ServiceName;
        this.logger = logger;
        this.timeProvider = TimeProvider.System;
        this.settings = settings ?? new CacheStoreSettings();

        if (!this.StateManager.TryAddStateSerializer(new CachedItemSerializer()))
        {
            throw new InvalidOperationException("Failed to set CachedItem custom serializer");
        }

        if (!this.StateManager.TryAddStateSerializer(new CacheStoreMetadataSerializer()))
        {
            throw new InvalidOperationException("Failed to set CacheStoreMetadata custom serializer");
        }
    }

    public BaseCacheStoreService(StatefulServiceContext context, CacheStoreSettings settings, IReliableStateManagerReplica2 reliableStateManagerReplica, TimeProvider timeProvider, ILogger<ICacheStoreService>? logger = null)
        : base(context, reliableStateManagerReplica)
    {
        this.serviceUri = context.ServiceName;
        this.logger = logger;
        this.timeProvider = timeProvider;
        this.settings = settings;
    }

    private IReliableDictionary<string, CachedItem> CacheStore =>
        _cacheStore ?? throw new InvalidOperationException("Cache store has not been initialized. Ensure OnOpenAsync completes before processing requests.");

    private IReliableDictionary<string, CacheStoreMetadata> CacheStoreMetadataDict =>
        _cacheStoreMetadata ?? throw new InvalidOperationException("Cache metadata store has not been initialized. Ensure OnOpenAsync completes before processing requests.");

    protected async override Task OnOpenAsync(ReplicaOpenMode openMode, CancellationToken cancellationToken)
    {
        using var client = new FabricClient();
        await client.PropertyManager.PutPropertyAsync(serviceUri, CacheStoreProperty, CacheStorePropertyValue, TimeSpan.FromSeconds(30), cancellationToken);
        partitionCount = (await client.QueryManager.GetPartitionListAsync(serviceUri, null, TimeSpan.FromSeconds(30), cancellationToken)).Count;
        _cacheStore = await StateManager.GetOrAddAsync<IReliableDictionary<string, CachedItem>>(CacheStoreConstants.CacheStoreName);
        _cacheStoreMetadata = await StateManager.GetOrAddAsync<IReliableDictionary<string, CacheStoreMetadata>>(CacheStoreConstants.CacheStoreMetadataName);
    }

    public async Task<byte[]> GetCachedItemAsync(string key)
    {
        var cacheStore = CacheStore;

        var cacheResult = await RetryHelper.ExecuteWithRetry(StateManager, async (tx, cancellationToken, state) =>
        {
            logger?.LogTrace("Get cached item called with key: {key} on partition id: {PartitionId}", key, Partition?.PartitionInfo.Id);
            return await cacheStore.TryGetValueAsync(tx, key);
        });

        if (cacheResult.HasValue)
        {
            var cachedItem = cacheResult.Value;
            var expireTime = cachedItem.AbsoluteExpiration;

            if (timeProvider.GetUtcNow() < expireTime)
            {
                // Update LRU position for every successful read.
                // For sliding-expiration items this also recalculates the absolute expiration.
                await SetCachedItemAsync(key, cachedItem.Value, cachedItem.SlidingExpiration, cachedItem.AbsoluteExpiration);

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
            var now = timeProvider.GetUtcNow();
            absoluteExpiration = now.AddMilliseconds(slidingExpiration.Value.TotalMilliseconds);
        }

        var cacheStore = CacheStore;
        var cacheStoreMetadata = CacheStoreMetadataDict;

        await RetryHelper.ExecuteWithRetry(StateManager, async (tx, cancellationToken, state) => 
        {
            logger?.LogTrace("Set cached item called with key: {key} on partition id: {PartitionId}", key, Partition?.PartitionInfo.Id);
       
            Func<string, Task<ConditionalValue<CachedItem>>> getCacheItem = async (string cacheKey) => await cacheStore.TryGetValueAsync(tx, cacheKey, LockMode.Update);
            var linkedDictionaryHelper = new LinkedDictionaryHelper(getCacheItem, this.settings.ByteSizeOffset);

            var cacheStoreInfoResult = await cacheStoreMetadata.TryGetValueAsync(tx, CacheStoreConstants.CacheStoreMetadataKey, LockMode.Update);
            var cacheStoreInfo = cacheStoreInfoResult.HasValue ? cacheStoreInfoResult.Value : new CacheStoreMetadata(0, null, null);
            var existingCacheItemResult = await getCacheItem(key);
            var existingCacheItem = existingCacheItemResult.HasValue ? existingCacheItemResult.Value : null;
            var cachedItem = ApplyAbsoluteExpiration(existingCacheItem, absoluteExpiration) ?? new CachedItem(value, null, null, slidingExpiration, absoluteExpiration);

            // empty linked dictionary
            if (cacheStoreInfo.FirstCacheKey == null)
            {
                var metadata = new CacheStoreMetadata(value.Length + this.settings.ByteSizeOffset, key, key);
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
        var cacheStore = CacheStore;
        var cacheStoreMetadata = CacheStoreMetadataDict;

        await RetryHelper.ExecuteWithRetry(StateManager, async (tx, cancellationToken, state) =>
        {
            logger?.LogTrace("Remove cached item called with key: {key} on partition id: {PartitionId}", key, Partition?.PartitionInfo.Id);

            var cacheResult = await cacheStore.TryRemoveAsync(tx, key);
            if (cacheResult.HasValue)
            {
                Func<string, Task<ConditionalValue<CachedItem>>> getCacheItem = async (string cacheKey) => await cacheStore.TryGetValueAsync(tx, cacheKey, LockMode.Update);
                var linkedDictionaryHelper = new LinkedDictionaryHelper(getCacheItem, this.settings.ByteSizeOffset);

                var cacheStoreInfoResult = await cacheStoreMetadata.TryGetValueAsync(tx, CacheStoreConstants.CacheStoreMetadataKey, LockMode.Update);
                var cacheStoreInfo = cacheStoreInfoResult.HasValue ? cacheStoreInfoResult.Value : new CacheStoreMetadata(0, null, null);
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
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RemoveLeastRecentlyUsedCacheItemWhenOverMaxSize(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogError(ex, "Unhandled exception in cache pruning loop; pruning will resume after next interval.");
            }

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
        var cacheStore = CacheStore;
        var cacheStoreMetadata = CacheStoreMetadataDict;
        bool continueRemovingItems = true;

        while (continueRemovingItems)
        {
            continueRemovingItems = false;
            cancellationToken.ThrowIfCancellationRequested();

            await RetryHelper.ExecuteWithRetry(StateManager, async (tx, cancelToken, state) =>
            {
                var metadata = await cacheStoreMetadata.TryGetValueAsync(tx, CacheStoreConstants.CacheStoreMetadataKey, LockMode.Update);

                if (!metadata.HasValue)
                {
                    return;
                }

                logger?.LogTrace("Size: {CurrentCacheSize}, MaxSize: {MaxCacheSize}", metadata.Value.Size, GetMaxSizeInBytes());

                if (metadata.Value.Size > GetMaxSizeInBytes())
                {
                    Func<string, Task<ConditionalValue<CachedItem>>> getCacheItem = async (string cacheKey) => await cacheStore.TryGetValueAsync(tx, cacheKey, LockMode.Update);
                    var linkedDictionaryHelper = new LinkedDictionaryHelper(getCacheItem, this.settings.ByteSizeOffset);

                    var firstItemKey = metadata.Value.FirstCacheKey;
                    var firstItemResult = await getCacheItem(firstItemKey);
                    if (!firstItemResult.HasValue)
                        throw new InvalidOperationException($"Cache item '{firstItemKey}' was expected but not found in the cache store.");
                    var firstCachedItem = firstItemResult.Value;

                    // Move item to last item if cached item is not expired
                    if (firstCachedItem.AbsoluteExpiration > timeProvider.GetUtcNow())
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
                        logger?.LogTrace("Auto Removing {key}", metadata.Value.FirstCacheKey);

                        var result = await linkedDictionaryHelper.Remove(metadata.Value, firstCachedItem);
                        await ApplyChanges(tx, cacheStore, cacheStoreMetadata, result);
                        await cacheStore.TryRemoveAsync(tx, metadata.Value.FirstCacheKey);

                        continueRemovingItems = result.CacheStoreMetadata.Size > GetMaxSizeInBytes();
                    }
                }
            });

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
