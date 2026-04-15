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

/// <summary>
/// Abstract base for a Service Fabric stateful service that hosts a distributed cache in Reliable Dictionaries,
/// with LRU eviction ordering and configurable size limits.
/// </summary>
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

    private long _maxSizeBytesPerPartition;

    // Metrics state: tracked size updated on every ApplyChanges, read by the gauge callback.
    // long is not a valid volatile field type in C#; Volatile.Read/Write are used for thread-safe access instead.
    private long _trackedSizeBytes;
    private string _partitionIdTag = string.Empty;

    private (Action<int> onRetry, Action onFinalFailure) _getCallbacks = (static _ => { }, static () => { });
    private (Action<int> onRetry, Action onFinalFailure) _setCallbacks = (static _ => { }, static () => { });
    private (Action<int> onRetry, Action onFinalFailure) _removeCallbacks = (static _ => { }, static () => { });
    private (Action<int> onRetry, Action onFinalFailure) _pruneCallbacks = (static _ => { }, static () => { });

    private ObservableGauge<long>? _sizeGauge;
    private ObservableGauge<long>? _sizeLimitGauge;

    /// <summary>The Reliable Dictionary storing cached items, keyed by cache key.</summary>
    protected IReliableDictionary<string, CachedItem>? _cacheStore;

    /// <summary>The Reliable Dictionary storing per-partition LRU metadata.</summary>
    protected IReliableDictionary<string, CacheStoreMetadata>? _cacheStoreMetadata;

    /// <summary>
    /// Initializes a new cache store service with optional settings and logger.
    /// </summary>
    /// <param name="context">The Service Fabric stateful service context.</param>
    /// <param name="settings">Optional cache store settings; defaults are used when <c>null</c>.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    public BaseCacheStoreService(StatefulServiceContext context, CacheStoreSettings? settings = null, ILogger<ICacheStoreService>? logger = null)
        : base(context)
    {
        this.serviceUri = context.ServiceName;
        this.logger = logger;
        this.timeProvider = TimeProvider.System;
        this.settings = settings ?? new CacheStoreSettings();
        ValidateSettings(this.settings);

        if (!this.StateManager.TryAddStateSerializer(new CachedItemSerializer()))
        {
            throw new InvalidOperationException("Failed to set CachedItem custom serializer");
        }

        if (!this.StateManager.TryAddStateSerializer(new CacheStoreMetadataSerializer()))
        {
            throw new InvalidOperationException("Failed to set CacheStoreMetadata custom serializer");
        }
    }

    /// <summary>
    /// Initializes a new cache store service with an explicit state manager and time provider, intended for unit testing.
    /// </summary>
    /// <param name="context">The Service Fabric stateful service context.</param>
    /// <param name="settings">Cache store settings.</param>
    /// <param name="reliableStateManagerReplica">The reliable state manager replica to use instead of the default.</param>
    /// <param name="timeProvider">The time provider used for expiration calculations.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    public BaseCacheStoreService(StatefulServiceContext context, CacheStoreSettings settings, IReliableStateManagerReplica2 reliableStateManagerReplica, TimeProvider timeProvider, ILogger<ICacheStoreService>? logger = null)
        : base(context, reliableStateManagerReplica)
    {
        this.serviceUri = context.ServiceName;
        this.logger = logger;
        this.timeProvider = timeProvider;
        this.settings = settings;
        ValidateSettings(this.settings);
        // partitionCount defaults to 1; OnOpenAsync updates it with the real count in production.
        _maxSizeBytesPerPartition = (settings.MaxCacheSize * BytesInMegabyte) / partitionCount;
    }

    private IReliableDictionary<string, CachedItem> CacheStore =>
        _cacheStore ?? throw new InvalidOperationException("Cache store has not been initialized. Ensure OnOpenAsync completes before processing requests.");

    private IReliableDictionary<string, CacheStoreMetadata> CacheStoreMetadataDict =>
        _cacheStoreMetadata ?? throw new InvalidOperationException("Cache metadata store has not been initialized. Ensure OnOpenAsync completes before processing requests.");

    private static void ValidateSettings(CacheStoreSettings settings)
    {
        if (settings.MaxCacheSize <= 0)
            throw new ArgumentException($"{nameof(CacheStoreSettings.MaxCacheSize)} must be greater than zero.", nameof(settings));
        if (settings.ByteSizeOffset < 0)
            throw new ArgumentException($"{nameof(CacheStoreSettings.ByteSizeOffset)} must be non-negative.", nameof(settings));
        if (settings.CachePruningInterval <= 0)
            throw new ArgumentException($"{nameof(CacheStoreSettings.CachePruningInterval)} must be greater than zero.", nameof(settings));
        if (string.IsNullOrWhiteSpace(settings.ListenerName))
            throw new ArgumentException($"{nameof(CacheStoreSettings.ListenerName)} must not be empty.", nameof(settings));
    }

    /// <summary>
    /// Registers the <c>CacheStore</c> service property, resolves the partition count,
    /// initializes the Reliable Dictionaries, and sets up metrics gauges.
    /// </summary>
    /// <param name="openMode">Indicates whether the replica is being opened as a new or existing replica.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
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
            () => new Measurement<long>(_maxSizeBytesPerPartition, new KeyValuePair<string, object?>("partition_id", _partitionIdTag)),
            "By",
            "Per-partition cache size limit");

        _maxSizeBytesPerPartition = (this.settings.MaxCacheSize * BytesInMegabyte) / partitionCount;

        _getCallbacks    = BuildRetryCallbacksForOperation("get");
        _setCallbacks    = BuildRetryCallbacksForOperation("set");
        _removeCallbacks = BuildRetryCallbacksForOperation("remove");
        _pruneCallbacks  = BuildRetryCallbacksForOperation("prune");
    }

    /// <inheritdoc/>
    protected override Task OnCloseAsync(CancellationToken cancellationToken)
    {
        // ObservableGauge<T> does not implement IDisposable; instruments share the Meter lifetime.
        // The fields are retained so a future SDK version that adds IDisposable can opt in here.
        return base.OnCloseAsync(cancellationToken);
    }

    /// <summary>
    /// Returns the cached bytes for <paramref name="key"/>, sliding the expiration window when applicable.
    /// Returns <c>null</c> and evicts the entry if expired.
    /// </summary>
    /// <param name="key">The cache key to retrieve.</param>
    /// <returns>The cached bytes, or <c>null</c> if absent or expired.</returns>
    public async Task<byte[]?> GetCachedItemAsync(string key)
    {
        var sw = Stopwatch.StartNew();
        var cacheStore = CacheStore;
        var (onRetry, onFinalFailure) = _getCallbacks;

        try
        {
            var cacheResult = await RetryHelper.ExecuteWithRetry(StateManager, async (tx, cancellationToken, state) =>
            {
                if (CacheEventSource.Log.IsEnabled())
                    CacheEventSource.Log.GetCacheItem(key, _partitionIdTag);
                return await cacheStore.TryGetValueAsync(tx, key);
            }, onRetry: onRetry, onFinalFailure: onFinalFailure);

            if (cacheResult.HasValue)
            {
                var cachedItem = cacheResult.Value;
                var expireTime = cachedItem.AbsoluteExpiration;

                if (expireTime == null || timeProvider.GetUtcNow() < expireTime.Value)
                {
                    CacheMetrics.Gets.Add(1, new TagList { { "result", "hit" }, { "partition_id", _partitionIdTag } });

                    // Only update LRU position for sliding-expiration items.
                    // Sliding-expiry items need SetCachedItemAsync to: (1) recalculate absoluteExpiration
                    // from the sliding window, and (2) move the item to the MRU end of the linked list.
                    // Absolute-expiry-only items expire by time, so skipping the write is safe and saves
                    // a full write transaction on every read (4.4× speedup measured in benchmarks).
                    if (cachedItem.SlidingExpiration.HasValue)
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
    
    /// <summary>
    /// Inserts or updates the entry for <paramref name="key"/>, promoting it to the MRU position.
    /// </summary>
    /// <param name="key">The cache key to store.</param>
    /// <param name="value">The raw bytes to cache.</param>
    /// <param name="slidingExpiration">Optional sliding expiration window; recalculates absolute expiry on each set.</param>
    /// <param name="absoluteExpiration">Optional hard expiry timestamp.</param>
    public async Task SetCachedItemAsync(string key, byte[] value, TimeSpan? slidingExpiration, DateTimeOffset? absoluteExpiration)
    {
        var sw = Stopwatch.StartNew();
        var (onRetry, onFinalFailure) = _setCallbacks;

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
                    CacheEventSource.Log.SetCacheItem(key, _partitionIdTag);
           
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

    /// <summary>
    /// Removes the entry for <paramref name="key"/> if it exists.
    /// </summary>
    /// <param name="key">The cache key to remove.</param>
    public Task RemoveCachedItemAsync(string key) => TryRemoveCachedItemAsync(key);

    /// <summary>
    /// Removes the item with the given key. Returns <c>true</c> if the item existed and was removed,
    /// <c>false</c> if it was already absent (e.g. removed by a concurrent operation).
    /// </summary>
    internal async Task<bool> TryRemoveCachedItemAsync(string key)
    {
        var sw = Stopwatch.StartNew();
        var cacheStore = CacheStore;
        var cacheStoreMetadata = CacheStoreMetadataDict;
        var (onRetry, onFinalFailure) = _removeCallbacks;
        var removed = false;

        try
        {
            await RetryHelper.ExecuteWithRetry(StateManager, async (tx, cancellationToken, state) =>
            {
                if (CacheEventSource.Log.IsEnabled())
                    CacheEventSource.Log.RemoveCacheItem(key, _partitionIdTag);

                var cacheResult = await cacheStore.TryRemoveAsync(tx, key);
                if (cacheResult.HasValue)
                {
                    Func<string, Task<ConditionalValue<CachedItem>>> getCacheItem = async (string cacheKey) => await cacheStore.TryGetValueAsync(tx, cacheKey, LockMode.Update);
                    var linkedDictionaryHelper = new LinkedDictionaryHelper(getCacheItem, this.settings.ByteSizeOffset);

                    var cacheStoreInfoResult = await cacheStoreMetadata.TryGetValueAsync(tx, CacheStoreConstants.CacheStoreMetadataKey, LockMode.Update);
                    var cacheStoreInfo = cacheStoreInfoResult.HasValue ? cacheStoreInfoResult.Value : new CacheStoreMetadata(0, null, null);
                    var result = await linkedDictionaryHelper.Remove(cacheStoreInfo, cacheResult.Value);

                    await ApplyChanges(tx, cacheStore, cacheStoreMetadata, result);
                    removed = true;
                }
            }, onRetry: onRetry, onFinalFailure: onFinalFailure);
        }
        finally
        {
            CacheMetrics.OperationDuration.Record(sw.Elapsed.TotalMilliseconds, new TagList { { "operation", "remove" }, { "partition_id", _partitionIdTag } });
        }

        return removed;
    }

    /// <summary>Creates the SF remoting listener for this cache store partition.</summary>
    /// <returns>An enumerable containing the single <see cref="ServiceReplicaListener"/> for this service.</returns>
    protected override IEnumerable<ServiceReplicaListener> CreateServiceReplicaListeners()
    {
        yield return new ServiceReplicaListener(context => new FabricTransportServiceRemotingListener(context, this), this.settings.ListenerName);
    }

    /// <summary>Runs the LRU pruning and expiration scan background loops until cancelled.</summary>
    /// <param name="cancellationToken">A token that signals the service is shutting down.</param>
    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(
            RunLruPruningLoopAsync(cancellationToken),
            RunExpirationScanLoopAsync(cancellationToken));
    }

    private async Task RunLruPruningLoopAsync(CancellationToken cancellationToken)
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

    private async Task RunExpirationScanLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(this.settings.ExpirationScanInterval), cancellationToken);

            try
            {
                await RemoveExpiredCacheItemsAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogError(ex, "Unhandled exception in expiration scan loop; scan will resume after next interval.");
            }
        }
    }

    /// <summary>
    /// Scans the linked dictionary for expired items and removes them proactively,
    /// independent of cache size pressure. Inspects at most
    /// <see cref="CacheStoreSettings.ExpirationScanBatchSize"/> items per call.
    /// </summary>
    internal async Task RemoveExpiredCacheItemsAsync(CancellationToken cancellationToken)
    {
        var cacheStore = CacheStore;
        var cacheStoreMetadata = CacheStoreMetadataDict;
        var now = timeProvider.GetUtcNow();
        var expiredKeys = new List<string>();

        // Phase 1: read-only walk of the linked list to collect expired keys.
        await RetryHelper.ExecuteWithRetry(StateManager, async (tx, ct, _) =>
        {
            expiredKeys.Clear();

            var metadataResult = await cacheStoreMetadata.TryGetValueAsync(tx, CacheStoreConstants.CacheStoreMetadataKey);
            if (!metadataResult.HasValue || metadataResult.Value.FirstCacheKey == null)
                return;

            var currentKey = metadataResult.Value.FirstCacheKey;
            var inspected = 0;

            while (currentKey != null && inspected < this.settings.ExpirationScanBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var itemResult = await cacheStore.TryGetValueAsync(tx, currentKey);
                if (!itemResult.HasValue)
                    break; // linked list integrity issue; stop safely

                var item = itemResult.Value;
                if (item.AbsoluteExpiration < now)
                    expiredKeys.Add(currentKey);

                currentKey = item.AfterCacheKey;
                inspected++;
            }

            logger?.LogDebug("Expiration scan on partition {PartitionId}: inspected {Inspected} item(s), found {Expired} expired.",
                _partitionIdTag, inspected, expiredKeys.Count);
        });

        // Phase 2: remove each expired key via the existing single-item retried write path.
        // TryRemoveCachedItemAsync returns false if the item was already removed by a concurrent
        // operation (LRU loop or lazy on-read expiry) — only emit the eviction metric on actual removal.
        foreach (var key in expiredKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            logger?.LogTrace("Expiration scan removing expired key: {Key} on partition {PartitionId}", key, _partitionIdTag);
            if (await TryRemoveCachedItemAsync(key))
                CacheMetrics.Evictions.Add(1, new TagList { { "reason", "expired" }, { "partition_id", _partitionIdTag } });
        }
    }

    /// <summary>
    /// Removes or re-queues the least-recently-used item when the partition exceeds its size limit.
    /// Expired items are evicted; non-expired items are moved to the MRU tail.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    protected async Task RemoveLeastRecentlyUsedCacheItemWhenOverMaxSize(CancellationToken cancellationToken)
    {
        var cacheStore = CacheStore;
        var cacheStoreMetadata = CacheStoreMetadataDict;
        bool continueRemovingItems = true;
        var (onRetry, onFinalFailure) = _pruneCallbacks;

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
                    CacheEventSource.Log.PruningCycleSize(metadata.Value.Size, _maxSizeBytesPerPartition);

                if (metadata.Value.Size > _maxSizeBytesPerPartition)
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

                        continueRemovingItems = addLastResult.CacheStoreMetadata.Size > _maxSizeBytesPerPartition;

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

                        continueRemovingItems = result.CacheStoreMetadata.Size > _maxSizeBytesPerPartition;

                        CacheMetrics.Evictions.Add(1, new TagList { { "reason", "expired" }, { "partition_id", _partitionIdTag } });
                    }
                }
            }, onRetry: onRetry, onFinalFailure: onFinalFailure);

        }
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
    /// Called once per operation type during <see cref="OnOpenAsync"/> to pre-cache the delegate
    /// instances as fields, eliminating per-call lambda allocations.
    /// </remarks>
    private (Action<int> onRetry, Action onFinalFailure) BuildRetryCallbacksForOperation(string operation) =>
    (
        attempt => CacheMetrics.TransactionRetries.Add(1, new TagList { { "operation", operation }, { "partition_id", _partitionIdTag } }),
        () => CacheMetrics.TransactionFailures.Add(1, new TagList { { "operation", operation }, { "partition_id", _partitionIdTag } })
    );
}

