namespace Ofn.ServiceFabric.Cache;

using System.Diagnostics;
using System.Diagnostics.Metrics;
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

    // Metrics state: tracked size updated on every ApplyChanges, read by the gauge callback.
    // long is not a valid volatile field type in C#; Volatile.Read/Write are used for thread-safe access instead.
    private long _trackedSizeBytes;
    private string _partitionIdTag = string.Empty;
    private ObservableGauge<long>? _sizeGauge;
    private ObservableGauge<long>? _sizeLimitGauge;

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

        _partitionIdTag = Partition.PartitionInfo.Id.ToString();

        // Register observable gauges for this partition's size and size limit.
        // Each gauge reports a single Measurement tagged with the partition ID so that
        // multi-partition deployments can be aggregated or filtered in the metrics backend.
        _sizeGauge = CacheMetrics.Meter.CreateObservableGauge<long>(
            "cache.size.bytes",
            () => new Measurement<long>(Volatile.Read(ref _trackedSizeBytes), new KeyValuePair<string, object?>("partition_id", _partitionIdTag)),
            "By",
            "Current total size of cached items in this partition");

        _sizeLimitGauge = CacheMetrics.Meter.CreateObservableGauge<long>(
            "cache.size.limit.bytes",
            () => new Measurement<long>(GetMaxSizeInBytes(), new KeyValuePair<string, object?>("partition_id", _partitionIdTag)),
            "By",
            "Per-partition cache size limit");
    }

    protected override Task OnCloseAsync(CancellationToken cancellationToken)
    {
        // ObservableGauge<T> does not implement IDisposable; instruments share the Meter lifetime.
        // The fields are retained so a future SDK version that adds IDisposable can opt in here.
        return base.OnCloseAsync(cancellationToken);
    }

    public async Task<byte[]?> GetCachedItemAsync(string key)
    {
        var sw = Stopwatch.StartNew();
        var cacheStore = CacheStore;
        var (onRetry, onFinalFailure) = BuildRetryCallbacks("get");

        try
        {
            var cacheResult = await RetryHelper.ExecuteWithRetry(StateManager, async (tx, cancellationToken, state) =>
            {
                if (CacheEventSource.Log.IsEnabled())
                    CacheEventSource.Log.GetCacheItem(key, Partition?.PartitionInfo.Id.ToString() ?? string.Empty);
                return await cacheStore.TryGetValueAsync(tx, key);
            }, onRetry: onRetry, onFinalFailure: onFinalFailure);

            if (cacheResult.HasValue)
            {
                var cachedItem = cacheResult.Value;
                var expireTime = cachedItem.AbsoluteExpiration;

                if (expireTime == null || timeProvider.GetUtcNow() < expireTime.Value)
                {
                    CacheMetrics.Gets.Add(1, new TagList { { "result", "hit" }, { "partition_id", _partitionIdTag } });

                    // Update LRU position for every successful read.
                    // For sliding-expiration items this also recalculates the absolute expiration.
                    await SetCachedItemAsync(key, cachedItem.Value, cachedItem.SlidingExpiration, cachedItem.AbsoluteExpiration);

                    return cachedItem.Value;
                }
                else // Remove expired items on access - its a bit weird but it works
                {
                    CacheMetrics.Gets.Add(1, new TagList { { "result", "expired" }, { "partition_id", _partitionIdTag } });
                    await RemoveCachedItemAsync(key);
                }
            }
            else
            {
                CacheMetrics.Gets.Add(1, new TagList { { "result", "miss" }, { "partition_id", _partitionIdTag } });
            }

            return null;
        }
        finally
        {
            CacheMetrics.OperationDuration.Record(sw.Elapsed.TotalMilliseconds, new TagList { { "operation", "get" }, { "partition_id", _partitionIdTag } });
        }
    }
    
    public async Task SetCachedItemAsync(string key, byte[] value, TimeSpan? slidingExpiration, DateTimeOffset? absoluteExpiration)
    {
        var sw = Stopwatch.StartNew();
        var (onRetry, onFinalFailure) = BuildRetryCallbacks("set");

        try
        {
            // Record item size on every set, including updates, as value length may differ.
            CacheMetrics.ItemSize.Record(value.Length, new TagList { { "partition_id", _partitionIdTag } });

            if (slidingExpiration.HasValue)
            {
                var now = timeProvider.GetUtcNow();
                absoluteExpiration = now.AddMilliseconds(slidingExpiration.Value.TotalMilliseconds);
            }

            var cacheStore = CacheStore;
            var cacheStoreMetadata = CacheStoreMetadataDict;

            await RetryHelper.ExecuteWithRetry(StateManager, async (tx, cancellationToken, state) => 
            {
                if (CacheEventSource.Log.IsEnabled())
                    CacheEventSource.Log.SetCacheItem(key, Partition?.PartitionInfo.Id.ToString() ?? string.Empty);
           
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
                    Volatile.Write(ref _trackedSizeBytes, metadata.Size);
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
            }, onRetry: onRetry, onFinalFailure: onFinalFailure);
        }
        finally
        {
            CacheMetrics.OperationDuration.Record(sw.Elapsed.TotalMilliseconds, new TagList { { "operation", "set" }, { "partition_id", _partitionIdTag } });
        }
    }

    public async Task RemoveCachedItemAsync(string key)
    {
        var sw = Stopwatch.StartNew();
        var cacheStore = CacheStore;
        var cacheStoreMetadata = CacheStoreMetadataDict;
        var (onRetry, onFinalFailure) = BuildRetryCallbacks("remove");

        try
        {
            await RetryHelper.ExecuteWithRetry(StateManager, async (tx, cancellationToken, state) =>
            {
                if (CacheEventSource.Log.IsEnabled())
                    CacheEventSource.Log.RemoveCacheItem(key, Partition?.PartitionInfo.Id.ToString() ?? string.Empty);

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
            }, onRetry: onRetry, onFinalFailure: onFinalFailure);
        }
        finally
        {
            CacheMetrics.OperationDuration.Record(sw.Elapsed.TotalMilliseconds, new TagList { { "operation", "remove" }, { "partition_id", _partitionIdTag } });
        }
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
        var (onRetry, onFinalFailure) = BuildRetryCallbacks("prune");

        // Count one pruning cycle per call to this method, regardless of how many items are removed.
        CacheMetrics.PruningCycles.Add(1, new TagList { { "partition_id", _partitionIdTag } });

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

                if (CacheEventSource.Log.IsEnabled())
                    CacheEventSource.Log.PruningCycleSize(metadata.Value.Size, GetMaxSizeInBytes());

                if (metadata.Value.Size > GetMaxSizeInBytes())
                {
                    Func<string, Task<ConditionalValue<CachedItem>>> getCacheItem = async (string cacheKey) => await cacheStore.TryGetValueAsync(tx, cacheKey, LockMode.Update);
                    var linkedDictionaryHelper = new LinkedDictionaryHelper(getCacheItem, this.settings.ByteSizeOffset);

                    var firstItemKey = metadata.Value.FirstCacheKey;
                    if (firstItemKey == null)
                        throw new InvalidOperationException("Cache metadata is inconsistent: size is non-zero but FirstCacheKey is null.");
                    var firstItemResult = await getCacheItem(firstItemKey);
                    if (!firstItemResult.HasValue)
                        throw new InvalidOperationException($"Cache item '{firstItemKey}' was expected but not found in the cache store.");
                    var firstCachedItem = firstItemResult.Value;

                    // Move item to last item if cached item is not expired
                    if (firstCachedItem.AbsoluteExpiration == null || firstCachedItem.AbsoluteExpiration.Value > timeProvider.GetUtcNow())
                    {
                        // remove cached item
                        var removeResult = await linkedDictionaryHelper.Remove(metadata.Value, firstCachedItem);
                        await ApplyChanges(tx, cacheStore, cacheStoreMetadata, removeResult);

                        // add to last
                        var addLastResult = await linkedDictionaryHelper.AddLast(removeResult.CacheStoreMetadata, firstItemKey, firstCachedItem, firstCachedItem.Value);
                        await ApplyChanges(tx, cacheStore, cacheStoreMetadata, addLastResult);

                        continueRemovingItems = addLastResult.CacheStoreMetadata.Size > GetMaxSizeInBytes();

                        if (CacheEventSource.Log.IsEnabled())
                            CacheEventSource.Log.PruningMoved(firstItemKey);

                        CacheMetrics.Evictions.Add(1, new TagList { { "reason", "lru" }, { "partition_id", _partitionIdTag } });
                    }
                    else  // Remove 
                    {
                        if (CacheEventSource.Log.IsEnabled())
                            CacheEventSource.Log.PruningEvicted(metadata.Value.FirstCacheKey!);

                        var result = await linkedDictionaryHelper.Remove(metadata.Value, firstCachedItem);
                        await ApplyChanges(tx, cacheStore, cacheStoreMetadata, result);
                        await cacheStore.TryRemoveAsync(tx, metadata.Value.FirstCacheKey!);

                        continueRemovingItems = result.CacheStoreMetadata.Size > GetMaxSizeInBytes();

                        CacheMetrics.Evictions.Add(1, new TagList { { "reason", "expired" }, { "partition_id", _partitionIdTag } });
                    }
                }
            }, onRetry: onRetry, onFinalFailure: onFinalFailure);

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

        // Keep the tracked size current so the observable gauge reflects the latest committed value.
        Volatile.Write(ref _trackedSizeBytes, linkedDictionaryItemsChanged.CacheStoreMetadata.Size);
    }

    private CachedItem? ApplyAbsoluteExpiration(CachedItem? cachedItem, DateTimeOffset? absoluteExpiration)
    {
        if (cachedItem != null)
        {
            return new CachedItem(cachedItem.Value, cachedItem.BeforeCacheKey, cachedItem.AfterCacheKey, cachedItem.SlidingExpiration, absoluteExpiration);
        }

        return null;
    }

    /// <summary>
    /// Builds a pair of retry callbacks for the given operation name that record
    /// <see cref="CacheMetrics.TransactionRetries"/> and <see cref="CacheMetrics.TransactionFailures"/>
    /// tagged with the operation and partition ID.
    /// </summary>
    /// <remarks>
    /// Centralises retry metric wiring so each call site stays DRY — just call
    /// <c>BuildRetryCallbacks("get")</c> and spread the tuple into the <c>RetryHelper</c> call.
    /// </remarks>
    private (Action<int> onRetry, Action onFinalFailure) BuildRetryCallbacks(string operation) =>
    (
        attempt => CacheMetrics.TransactionRetries.Add(1, new TagList { { "operation", operation }, { "partition_id", _partitionIdTag } }),
        () => CacheMetrics.TransactionFailures.Add(1, new TagList { { "operation", operation }, { "partition_id", _partitionIdTag } })
    );
}

