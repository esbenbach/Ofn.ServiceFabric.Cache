namespace Ofn.ServiceFabric.Cache.UnitTests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture.Xunit3;
using Microsoft.Extensions.Logging;
using Microsoft.ServiceFabric.Data;
using Microsoft.ServiceFabric.Data.Collections;
using Moq;
using Ofn.ServiceFabric.Cache;
using Ofn.ServiceFabric.Cache.Abstractions;
using Xunit;

/// <summary>
/// Tests that <see cref="BaseCacheStoreService"/> emits the expected
/// System.Diagnostics.Metrics measurements for each cache operation.
/// </summary>
[Collection("Metrics")]
public class CacheMetricsTest
{
    // ──────────────────────────────────────────────────────────────────────────
    // Listener helper
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="MeterListener"/> that captures all measurements
    /// from the server-side "Ofn.ServiceFabric.Cache" meter and returns both
    /// the listener (for disposal) and the recording list.
    /// The listener is already started when returned.
    /// </summary>
    private static (MeterListener Listener, ConcurrentBag<(string Name, object Value, KeyValuePair<string, object?>[] Tags)> Recordings)
        CreateListener()
    {
        var recordings = new ConcurrentBag<(string Name, object Value, KeyValuePair<string, object?>[] Tags)>();
        var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Ofn.ServiceFabric.Cache")
                l.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            recordings.Add((instrument.Name, (object)measurement, tags.ToArray())));

        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            recordings.Add((instrument.Name, (object)measurement, tags.ToArray())));

        listener.Start();
        return (listener, recordings);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // In-memory store helper (mirrors BaseCacheStoreServiceTest.SetupInMemoryStores)
    // ──────────────────────────────────────────────────────────────────────────

    private static Dictionary<TKey, TValue> SetupInMemoryStores<TKey, TValue>(
        Mock<IReliableStateManagerReplica2> stateManager,
        Mock<IReliableDictionary<TKey, TValue>> reliableDict,
        BaseCacheStoreServiceTest.StubCacheStoreService? stub = null)
        where TKey : IComparable<TKey>, IEquatable<TKey>
    {
        var inMemoryDict = new Dictionary<TKey, TValue>();
        ConditionalValue<TValue> getItem(TKey key) =>
            inMemoryDict.TryGetValue(key, out TValue? value)
                ? new ConditionalValue<TValue>(true, value)
                : new ConditionalValue<TValue>(false, default!);

        stateManager
            .Setup(m => m.GetOrAddAsync<IReliableDictionary<TKey, TValue>>(It.IsAny<string>()))
            .Returns(Task.FromResult(reliableDict.Object));

        reliableDict
            .Setup(m => m.TryGetValueAsync(It.IsAny<ITransaction>(), It.IsAny<TKey>()))
            .Returns((ITransaction _, TKey k) => Task.FromResult(getItem(k)));

        reliableDict
            .Setup(m => m.TryGetValueAsync(It.IsAny<ITransaction>(), It.IsAny<TKey>(), It.IsAny<LockMode>()))
            .Returns((ITransaction _, TKey k, LockMode _) => Task.FromResult(getItem(k)));

        reliableDict
            .Setup(m => m.SetAsync(It.IsAny<ITransaction>(), It.IsAny<TKey>(), It.IsAny<TValue>()))
            .Returns((ITransaction _, TKey k, TValue v) => { inMemoryDict[k] = v; return Task.CompletedTask; });

        reliableDict
            .Setup(m => m.TryRemoveAsync(It.IsAny<ITransaction>(), It.IsAny<TKey>()))
            .Returns((ITransaction _, TKey k) => { var r = getItem(k); inMemoryDict.Remove(k); return Task.FromResult(r); });

        if (stub is not null)
        {
            if (reliableDict.Object is IReliableDictionary<string, CachedItem> cacheItemStore)
                stub.InitCacheStore(cacheItemStore);
            else if (reliableDict.Object is IReliableDictionary<string, CacheStoreMetadata> metadataStore)
                stub.InitCacheStoreMetadata(metadataStore);
        }

        return inMemoryDict;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Tag assertion helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static bool HasTag(KeyValuePair<string, object?>[] tags, string key, string value) =>
        Array.Exists(tags, t => t.Key == key && (string?)t.Value == value);

    // ──────────────────────────────────────────────────────────────────────────
    // Test 1 — GetCachedItemAsync: existing non-expired item records hit
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task GetCachedItemAsync_ExistingNonExpiredItem_RecordsHit(
        [Frozen] Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen] Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen] Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen] FakeTimeProvider timeProvider,
        [Greedy] BaseCacheStoreServiceTest.StubCacheStoreService cacheStore)
    {
        var currentTime = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        timeProvider.SetUtcNow(currentTime);

        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        var value = Encoding.UTF8.GetBytes("hello");
        await cacheStore.SetCachedItemAsync("key1", value, TimeSpan.FromSeconds(60), null);

        var (listener, recordings) = CreateListener();
        using (listener)
        {
            var result = await cacheStore.GetCachedItemAsync("key1");

            Assert.NotNull(result);

            // cache.gets with result=hit must be recorded
            var hit = recordings.FirstOrDefault(r =>
                r.Name == "cache.gets" && HasTag(r.Tags, "result", "hit"));
            Assert.NotEqual(default, hit);
            Assert.Equal(1L, (long)hit.Value);

            // cache.operation.duration with operation=get must be recorded
            Assert.Contains(recordings, r =>
                r.Name == "cache.operation.duration" && HasTag(r.Tags, "operation", "get"));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 2 — GetCachedItemAsync: missing item records miss
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task GetCachedItemAsync_MissingItem_RecordsMiss(
        [Frozen] Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen] Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen] Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen] FakeTimeProvider timeProvider,
        [Greedy] BaseCacheStoreServiceTest.StubCacheStoreService cacheStore)
    {
        timeProvider.SetUtcNow(DateTimeOffset.UtcNow);

        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        var (listener, recordings) = CreateListener();
        using (listener)
        {
            var result = await cacheStore.GetCachedItemAsync("no-such-key");

            Assert.Null(result);

            var miss = recordings.FirstOrDefault(r =>
                r.Name == "cache.gets" && HasTag(r.Tags, "result", "miss"));
            Assert.NotEqual(default, miss);
            Assert.Equal(1L, (long)miss.Value);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 3 — GetCachedItemAsync: expired item records expired
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task GetCachedItemAsync_ExpiredItem_RecordsExpired(
        [Frozen] Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen] Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen] Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen] FakeTimeProvider timeProvider,
        [Greedy] BaseCacheStoreServiceTest.StubCacheStoreService cacheStore)
    {
        var currentTime = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        timeProvider.SetUtcNow(currentTime);

        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        var value = Encoding.UTF8.GetBytes("hello");
        // Absolute expiration in the past — item is already expired
        var expiredAt = currentTime.AddSeconds(-1);
        await cacheStore.SetCachedItemAsync("expiredKey", value, null, expiredAt);

        var (listener, recordings) = CreateListener();
        using (listener)
        {
            var result = await cacheStore.GetCachedItemAsync("expiredKey");

            Assert.Null(result);

            var expired = recordings.FirstOrDefault(r =>
                r.Name == "cache.gets" && HasTag(r.Tags, "result", "expired"));
            Assert.NotEqual(default, expired);
            Assert.Equal(1L, (long)expired.Value);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 4 — SetCachedItemAsync: new item records duration and item size
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task SetCachedItemAsync_NewItem_RecordsOperationDurationAndItemSize(
        [Frozen] Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen] Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen] Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen] FakeTimeProvider timeProvider,
        [Greedy] BaseCacheStoreServiceTest.StubCacheStoreService cacheStore)
    {
        timeProvider.SetUtcNow(DateTimeOffset.UtcNow);

        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        var payload = new byte[128];

        var (listener, recordings) = CreateListener();
        using (listener)
        {
            await cacheStore.SetCachedItemAsync("newKey", payload, TimeSpan.FromSeconds(60), null);

            // cache.operation.duration with operation=set
            Assert.Contains(recordings, r =>
                r.Name == "cache.operation.duration" && HasTag(r.Tags, "operation", "set"));

            // cache.item.size records the byte array length
            var sizeRec = recordings.FirstOrDefault(r => r.Name == "cache.item.size");
            Assert.NotEqual(default, sizeRec);
            Assert.Equal((long)payload.Length, (long)sizeRec.Value);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 5 — RemoveCachedItemAsync: records operation duration for remove
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task RemoveCachedItemAsync_ExistingItem_RecordsOperationDuration(
        [Frozen] Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen] Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen] Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen] FakeTimeProvider timeProvider,
        [Greedy] BaseCacheStoreServiceTest.StubCacheStoreService cacheStore)
    {
        timeProvider.SetUtcNow(DateTimeOffset.UtcNow);

        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        await cacheStore.SetCachedItemAsync("toRemove", new byte[] { 1, 2, 3 }, TimeSpan.FromSeconds(30), null);

        var (listener, recordings) = CreateListener();
        using (listener)
        {
            await cacheStore.RemoveCachedItemAsync("toRemove");

            Assert.Contains(recordings, r =>
                r.Name == "cache.operation.duration" && HasTag(r.Tags, "operation", "remove"));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 6 — RemoveLeastRecentlyUsedCacheItemWhenOverMaxSize: records pruning cycle
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task RemoveLeastRecentlyUsedCacheItemWhenOverMaxSize_Called_RecordsPruningCycle(
        [Frozen] Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen] Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen] Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen] FakeTimeProvider timeProvider,
        [Greedy] BaseCacheStoreServiceTest.StubCacheStoreService cacheStore)
    {
        timeProvider.SetUtcNow(DateTimeOffset.UtcNow);

        // Empty store — pruning cycle is recorded even when there is nothing to remove
        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        var (listener, recordings) = CreateListener();
        using (listener)
        {
            await cacheStore.RemoveLeastRecentlyUsedCacheItemWhenOverMaxSize();

            var cycle = recordings.FirstOrDefault(r => r.Name == "cache.pruning.cycles");
            Assert.NotEqual(default, cycle);
            Assert.Equal(1L, (long)cycle.Value);
            Assert.Contains(cycle.Tags, t => t.Key == "partition_id");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 7 — Pruning: expired first item → records expired eviction
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task RemoveLeastRecentlyUsedCacheItemWhenOverMaxSize_OverCapacityExpiredFirstItem_RecordsExpiredEviction(
        [Frozen] Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen] Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen] Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen] FakeTimeProvider timeProvider,
        [Greedy] BaseCacheStoreServiceTest.StubCacheStoreService cacheStore)
    {
        // MaxCacheSize = 1 MB (1 048 576 bytes). StubCacheStoreService uses MaxCacheSize=1.
        // item1: 800 000 bytes + 250 offset = 800 250
        // item2: 200 000 bytes + 250 offset = 200 250
        // item3: 200 000 bytes + 250 offset = 200 250
        // total = 1 200 750 > 1 048 576 → over capacity
        // After removing item1 (expired): 400 500 < 1 048 576 → stop
        var currentTime = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        timeProvider.SetUtcNow(currentTime);

        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        var largeValue = new byte[800_000];
        var smallValue = new byte[200_000];

        // Item 1 is added first (so it is at the LRU front) with an expiration in the past
        await cacheStore.SetCachedItemAsync("item1", largeValue, null, currentTime.AddSeconds(-1));
        // Items 2 and 3 are not expired
        await cacheStore.SetCachedItemAsync("item2", smallValue, TimeSpan.FromMinutes(10), null);
        await cacheStore.SetCachedItemAsync("item3", smallValue, TimeSpan.FromMinutes(10), null);

        var (listener, recordings) = CreateListener();
        using (listener)
        {
            await cacheStore.RemoveLeastRecentlyUsedCacheItemWhenOverMaxSize();

            var expiredEviction = recordings.FirstOrDefault(r =>
                r.Name == "cache.evictions" && HasTag(r.Tags, "reason", "expired"));
            Assert.NotEqual(default, expiredEviction);
            Assert.Equal(1L, (long)expiredEviction.Value);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 8 — Pruning: non-expired first item → records LRU eviction
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task RemoveLeastRecentlyUsedCacheItemWhenOverMaxSize_OverCapacityNonExpiredFirstItem_RecordsLruEviction(
        [Frozen] Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen] Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen] Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen] FakeTimeProvider timeProvider,
        [Greedy] BaseCacheStoreServiceTest.StubCacheStoreService cacheStore)
    {
        // item1: 800 000 bytes, NOT expired (10-minute expiry) → at LRU front → LRU eviction
        // items 2-3: 200 000 bytes each, 1-second sliding expiry → will be expired after time advance
        // total = 1 200 750 > 1 048 576 → over capacity
        // Pruning iteration 1: item1 not expired → LRU eviction, moved to last
        // Pruning iteration 2: item2 expired → removed, size drops to 1 000 500 < 1 048 576 → stop
        var currentTime = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        timeProvider.SetUtcNow(currentTime);

        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        var largeValue = new byte[800_000];
        var smallValue = new byte[200_000];

        // Item 1 first (LRU front) with a long expiry — will NOT be expired at pruning time
        await cacheStore.SetCachedItemAsync("item1", largeValue, TimeSpan.FromMinutes(10), null);
        // Items 2 and 3 with 1-second sliding expiry
        await cacheStore.SetCachedItemAsync("item2", smallValue, TimeSpan.FromSeconds(1), null);
        await cacheStore.SetCachedItemAsync("item3", smallValue, TimeSpan.FromSeconds(1), null);

        // Advance time so items 2 and 3 are expired; item1 still valid
        timeProvider.SetUtcNow(currentTime.AddSeconds(2));

        var (listener, recordings) = CreateListener();
        using (listener)
        {
            await cacheStore.RemoveLeastRecentlyUsedCacheItemWhenOverMaxSize();

            var lruEviction = recordings.FirstOrDefault(r =>
                r.Name == "cache.evictions" && HasTag(r.Tags, "reason", "lru"));
            Assert.NotEqual(default, lruEviction);
            Assert.Equal(1L, (long)lruEviction.Value);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Tests 9-10 — RetryHelper callback parameters
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RetryHelper_OnRetryCallback_CalledOnTimeoutRetry()
    {
        int callCount = 0;
        int retryCount = 0;
        int? retryAttemptArg = null;

        Func<CancellationToken, object?, Task<int>> operation = (_, _) =>
        {
            callCount++;
            if (callCount == 1)
                throw new TimeoutException();
            return Task.FromResult(42);
        };

        var result = await RetryHelper.ExecuteWithRetry(
            operation,
            maxAttempts: 3,
            initialDelay: TimeSpan.FromMilliseconds(1),
            onRetry: attempt => { retryCount++; retryAttemptArg = attempt; },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
        Assert.Equal(1, retryCount);
        Assert.Equal(0, retryAttemptArg); // first attempt is index 0
    }

    [Fact]
    public async Task RetryHelper_OnFinalFailureCallback_CalledWhenAllAttemptsExhausted()
    {
        int retryCount = 0;
        bool finalFailureCalled = false;

        Func<CancellationToken, object?, Task<int>> operation = (_, _) =>
            throw new TimeoutException("always fails");

        await Assert.ThrowsAsync<TimeoutException>(() =>
            RetryHelper.ExecuteWithRetry(
                operation,
                maxAttempts: 1,
                initialDelay: TimeSpan.FromMilliseconds(1),
                onRetry: _ => retryCount++,
                onFinalFailure: () => finalFailureCalled = true,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(finalFailureCalled);
        // With maxAttempts=1 the single attempt goes straight to onFinalFailure; onRetry is never called
        Assert.Equal(0, retryCount);
    }
}
