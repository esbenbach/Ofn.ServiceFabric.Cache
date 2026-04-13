namespace Ofn.ServiceFabric.Cache.UnitTests;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Fabric;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture.Xunit3;
using Microsoft.ServiceFabric.Data;
using Microsoft.ServiceFabric.Data.Collections;
using Moq;
using Ofn.ServiceFabric.Cache;
using Xunit;

/// <summary>
/// Tests for <see cref="BaseCacheStoreService.RemoveExpiredCacheItemsAsync"/>.
/// </summary>
public class ExpirationScanTest
{
    // ──────────────────────────────────────────────────────────────────────────
    // Helpers (mirror pattern from BaseCacheStoreServiceTest / CacheMetricsTest)
    // ──────────────────────────────────────────────────────────────────────────

    private static Dictionary<TKey, TValue> SetupInMemoryStores<TKey, TValue>(
        Mock<IReliableStateManagerReplica2> stateManager,
        Mock<IReliableDictionary<TKey, TValue>> reliableDict,
        BaseCacheStoreServiceTest.StubCacheStoreService? stub = null)
        where TKey : IComparable<TKey>, IEquatable<TKey>
    {
        var dict = new Dictionary<TKey, TValue>();
        ConditionalValue<TValue> Get(TKey key) =>
            dict.TryGetValue(key, out var v)
                ? new ConditionalValue<TValue>(true, v)
                : new ConditionalValue<TValue>(false, default!);

        stateManager
            .Setup(m => m.GetOrAddAsync<IReliableDictionary<TKey, TValue>>(It.IsAny<string>()))
            .Returns(Task.FromResult(reliableDict.Object));

        reliableDict
            .Setup(m => m.TryGetValueAsync(It.IsAny<ITransaction>(), It.IsAny<TKey>()))
            .Returns((ITransaction _, TKey k) => Task.FromResult(Get(k)));

        reliableDict
            .Setup(m => m.TryGetValueAsync(It.IsAny<ITransaction>(), It.IsAny<TKey>(), It.IsAny<LockMode>()))
            .Returns((ITransaction _, TKey k, LockMode _) => Task.FromResult(Get(k)));

        reliableDict
            .Setup(m => m.SetAsync(It.IsAny<ITransaction>(), It.IsAny<TKey>(), It.IsAny<TValue>()))
            .Returns((ITransaction _, TKey k, TValue v) => { dict[k] = v; return Task.CompletedTask; });

        reliableDict
            .Setup(m => m.TryRemoveAsync(It.IsAny<ITransaction>(), It.IsAny<TKey>()))
            .Returns((ITransaction _, TKey k) => { var r = Get(k); dict.Remove(k); return Task.FromResult(r); });

        if (stub is not null)
        {
            if (reliableDict.Object is IReliableDictionary<string, CachedItem> ci)
                stub.InitCacheStore(ci);
            else if (reliableDict.Object is IReliableDictionary<string, CacheStoreMetadata> meta)
                stub.InitCacheStoreMetadata(meta);
        }

        return dict;
    }

    private static (MeterListener Listener, List<(string Name, object Value, KeyValuePair<string, object?>[] Tags)> Recordings)
        CreateEvictionListener()
    {
        var recordings = new List<(string Name, object Value, KeyValuePair<string, object?>[] Tags)>();
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Ofn.ServiceFabric.Cache")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            recordings.Add((instrument.Name, (object)measurement, tags.ToArray())));
        listener.Start();
        return (listener, recordings);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // T1 — empty store: completes without error, no items removed
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task RemoveExpiredCacheItemsAsync_EmptyStore_CompletesWithoutError(
        [Frozen] Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen] Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen] Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen] FakeTimeProvider timeProvider,
        [Greedy] BaseCacheStoreServiceTest.StubCacheStoreService cacheStore)
    {
        timeProvider.SetUtcNow(DateTimeOffset.UtcNow);
        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        var metadata = SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        // No items set — metadata dictionary is empty
        await cacheStore.RemoveExpiredCacheItemsPublic(TestContext.Current.CancellationToken);

        Assert.Empty(metadata);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // T2 — all items expired: all are removed
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task RemoveExpiredCacheItemsAsync_AllExpired_AllItemsRemoved(
        [Frozen] Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen] Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen] Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen] FakeTimeProvider timeProvider,
        [Greedy] BaseCacheStoreServiceTest.StubCacheStoreService cacheStore)
    {
        var now = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        timeProvider.SetUtcNow(now);
        var expiredAt = now.AddSeconds(-1);

        var cachedItems = SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        var value = Encoding.UTF8.GetBytes("v");
        await cacheStore.SetCachedItemAsync("key1", value, null, expiredAt);
        await cacheStore.SetCachedItemAsync("key2", value, null, expiredAt);
        await cacheStore.SetCachedItemAsync("key3", value, null, expiredAt);

        Assert.Equal(3, cachedItems.Count);

        await cacheStore.RemoveExpiredCacheItemsPublic(TestContext.Current.CancellationToken);

        Assert.Empty(cachedItems);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // T3 — no items expired: nothing is removed
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task RemoveExpiredCacheItemsAsync_NoneExpired_NothingRemoved(
        [Frozen] Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen] Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen] Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen] FakeTimeProvider timeProvider,
        [Greedy] BaseCacheStoreServiceTest.StubCacheStoreService cacheStore)
    {
        var now = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        timeProvider.SetUtcNow(now);
        var expiresInFuture = now.AddMinutes(5);

        var cachedItems = SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        var value = Encoding.UTF8.GetBytes("v");
        await cacheStore.SetCachedItemAsync("key1", value, null, expiresInFuture);
        await cacheStore.SetCachedItemAsync("key2", value, null, expiresInFuture);

        Assert.Equal(2, cachedItems.Count);

        await cacheStore.RemoveExpiredCacheItemsPublic(TestContext.Current.CancellationToken);

        Assert.Equal(2, cachedItems.Count);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // T4 — mixed: only expired items are removed
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task RemoveExpiredCacheItemsAsync_MixedItems_OnlyExpiredRemoved(
        [Frozen] Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen] Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen] Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen] FakeTimeProvider timeProvider,
        [Greedy] BaseCacheStoreServiceTest.StubCacheStoreService cacheStore)
    {
        var now = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        timeProvider.SetUtcNow(now);

        var cachedItems = SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        var value = Encoding.UTF8.GetBytes("v");
        await cacheStore.SetCachedItemAsync("expired1", value, null, now.AddSeconds(-5));
        await cacheStore.SetCachedItemAsync("live1",    value, null, now.AddMinutes(5));
        await cacheStore.SetCachedItemAsync("expired2", value, null, now.AddSeconds(-1));
        await cacheStore.SetCachedItemAsync("live2",    value, null, now.AddMinutes(10));

        await cacheStore.RemoveExpiredCacheItemsPublic(TestContext.Current.CancellationToken);

        Assert.False(cachedItems.ContainsKey("expired1"));
        Assert.False(cachedItems.ContainsKey("expired2"));
        Assert.True(cachedItems.ContainsKey("live1"));
        Assert.True(cachedItems.ContainsKey("live2"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // T5 — batch size exactly matches item count: all processed in one cycle
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task RemoveExpiredCacheItemsAsync_ExactBatchSize_AllExpiredRemoved(
        [Frozen] Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen] Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen] Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen] FakeTimeProvider timeProvider,
        StatefulServiceContext context)
    {
        var now = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        timeProvider.SetUtcNow(now);

        var settings = new CacheStoreSettings { MaxCacheSize = 100, CachePruningInterval = 60, ExpirationScanBatchSize = 3 };
        var cacheStore = new BaseCacheStoreServiceTest.CustomSettingsStubPublic(context, settings, stateManager.Object, timeProvider);

        var cachedItems = SetupInMemoryStores(stateManager, cacheItemDict);
        SetupInMemoryStores(stateManager, metadataDict);
        cacheStore.InitCacheStore(cacheItemDict.Object);
        cacheStore.InitCacheStoreMetadata(metadataDict.Object);

        var value = Encoding.UTF8.GetBytes("v");
        // Exactly 3 items — equals the batch size
        await cacheStore.SetCachedItemAsync("key1", value, null, now.AddSeconds(-1));
        await cacheStore.SetCachedItemAsync("key2", value, null, now.AddSeconds(-1));
        await cacheStore.SetCachedItemAsync("key3", value, null, now.AddSeconds(-1));

        await cacheStore.RemoveExpiredCacheItemsPublic(TestContext.Current.CancellationToken);

        Assert.Empty(cachedItems);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // T6 — items exceed batch size: only BatchSize items inspected per cycle
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task RemoveExpiredCacheItemsAsync_ItemsExceedBatchSize_OnlyBatchSizeInspected(
        [Frozen] Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen] Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen] Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen] FakeTimeProvider timeProvider,
        StatefulServiceContext context)
    {
        var now = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        timeProvider.SetUtcNow(now);

        var settings = new CacheStoreSettings { MaxCacheSize = 100, CachePruningInterval = 60, ExpirationScanBatchSize = 2 };
        var cacheStore = new BaseCacheStoreServiceTest.CustomSettingsStubPublic(context, settings, stateManager.Object, timeProvider);

        var cachedItems = SetupInMemoryStores(stateManager, cacheItemDict);
        SetupInMemoryStores(stateManager, metadataDict);
        cacheStore.InitCacheStore(cacheItemDict.Object);
        cacheStore.InitCacheStoreMetadata(metadataDict.Object);

        var value = Encoding.UTF8.GetBytes("v");
        // 4 expired items, but batch size is 2 — only 2 removed per cycle
        await cacheStore.SetCachedItemAsync("key1", value, null, now.AddSeconds(-1));
        await cacheStore.SetCachedItemAsync("key2", value, null, now.AddSeconds(-1));
        await cacheStore.SetCachedItemAsync("key3", value, null, now.AddSeconds(-1));
        await cacheStore.SetCachedItemAsync("key4", value, null, now.AddSeconds(-1));

        await cacheStore.RemoveExpiredCacheItemsPublic(TestContext.Current.CancellationToken);

        // After one cycle: 2 removed, 2 remain
        Assert.Equal(2, cachedItems.Count);

        // Second cycle removes the rest
        await cacheStore.RemoveExpiredCacheItemsPublic(TestContext.Current.CancellationToken);

        Assert.Empty(cachedItems);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // T8 — cancellation: exits cleanly
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task RemoveExpiredCacheItemsAsync_CancellationRequested_ThrowsOperationCanceledException(
        [Frozen] Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen] Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen] Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen] FakeTimeProvider timeProvider,
        [Greedy] BaseCacheStoreServiceTest.StubCacheStoreService cacheStore)
    {
        var now = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        timeProvider.SetUtcNow(now);

        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        var value = Encoding.UTF8.GetBytes("v");
        await cacheStore.SetCachedItemAsync("key1", value, null, now.AddSeconds(-1));
        await cacheStore.SetCachedItemAsync("key2", value, null, now.AddSeconds(-1));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cacheStore.RemoveExpiredCacheItemsPublic(cts.Token));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // T9 — metrics: cache.evictions{reason=expired} incremented per removed item
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task RemoveExpiredCacheItemsAsync_ExpiredItems_EvictionMetricIncremented(
        [Frozen] Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen] Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen] Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen] FakeTimeProvider timeProvider,
        [Greedy] BaseCacheStoreServiceTest.StubCacheStoreService cacheStore)
    {
        var now = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        timeProvider.SetUtcNow(now);

        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        var value = Encoding.UTF8.GetBytes("v");
        await cacheStore.SetCachedItemAsync("expired1", value, null, now.AddSeconds(-2));
        await cacheStore.SetCachedItemAsync("live1",    value, null, now.AddMinutes(5));
        await cacheStore.SetCachedItemAsync("expired2", value, null, now.AddSeconds(-1));

        var (listener, recordings) = CreateEvictionListener();
        using (listener)
        {
            await cacheStore.RemoveExpiredCacheItemsPublic(TestContext.Current.CancellationToken);

            var evictions = recordings.FindAll(r =>
                r.Name == "cache.evictions" &&
                Array.Exists(r.Tags, t => t.Key == "reason" && (string?)t.Value == "expired"));

            // Two expired items → two eviction measurements
            Assert.Equal(2, evictions.Count);
            Assert.All(evictions, e => Assert.Equal(1L, (long)e.Value));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // T10 — no double-counting: TryRemoveCachedItemAsync returns false and
    //        the scan does not emit the eviction metric for an absent key
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task TryRemoveCachedItemAsync_KeyAlreadyAbsent_ReturnsFalseAndNoEvictionMetric(
        [Frozen] Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen] Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen] Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen] FakeTimeProvider timeProvider,
        [Greedy] BaseCacheStoreServiceTest.StubCacheStoreService cacheStore)
    {
        var now = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        timeProvider.SetUtcNow(now);

        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        var (listener, recordings) = CreateEvictionListener();
        using (listener)
        {
            // Key was never set — TryRemoveCachedItemAsync must return false
            var removed = await cacheStore.TryRemoveCachedItemPublic("key-that-never-existed");
            Assert.False(removed);

            // The expiration scan must not emit an eviction metric for keys that are absent
            var evictions = recordings.FindAll(r => r.Name == "cache.evictions");
            Assert.Empty(evictions);
        }
    }
}
