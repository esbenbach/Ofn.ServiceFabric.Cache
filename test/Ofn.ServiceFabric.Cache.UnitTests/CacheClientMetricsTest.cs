namespace Ofn.ServiceFabric.Cache.UnitTests;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading.Tasks;
using AutoFixture.Xunit3;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Moq;
using Ofn.ServiceFabric.Cache.Abstractions;
using Ofn.ServiceFabric.Cache.Client;
using Xunit;

/// <summary>
/// Tests that <see cref="ServiceFabricDistributedCache"/> emits the expected
/// System.Diagnostics.Metrics measurements for each client-side operation.
/// </summary>
public class CacheClientMetricsTest
{
    private static readonly Guid TestCacheStoreId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ──────────────────────────────────────────────────────────────────────────
    // Factory helper (mirrors ServiceFabricDistributedCacheTest.CreateCache)
    // ──────────────────────────────────────────────────────────────────────────

    private static ServiceFabricDistributedCache CreateCache(
        IDistributedCacheStoreLocator locator,
        TimeProvider timeProvider,
        Guid? cacheStoreId = null,
        TimeSpan? defaultSlidingExpiration = null,
        bool useDefaultSlidingExpiration = true)
    {
        var options = Options.Create(new ServiceFabricCacheOptions
        {
            CacheStoreId = cacheStoreId ?? TestCacheStoreId,
            DefaultSlidingExpiration = useDefaultSlidingExpiration
                ? (defaultSlidingExpiration ?? TimeSpan.FromSeconds(60))
                : null
        });
        return new ServiceFabricDistributedCache(options, locator, timeProvider);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Listener helper
    // ──────────────────────────────────────────────────────────────────────────

    private static (MeterListener Listener, List<(string Name, object Value, KeyValuePair<string, object?>[] Tags)> Recordings)
        CreateListener()
    {
        var recordings = new List<(string Name, object Value, KeyValuePair<string, object?>[] Tags)>();
        var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Ofn.ServiceFabric.Cache.Client")
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
    // Tag assertion helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static bool HasTag(KeyValuePair<string, object?>[] tags, string key, string value) =>
        Array.Exists(tags, t => t.Key == key && (string?)t.Value == value);

    // ──────────────────────────────────────────────────────────────────────────
    // Test 11 — GetAsync: proxy returns value → records hit and duration
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task GetAsync_ProxyReturnsValue_RecordsHitAndDuration(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var proxy = new Mock<ICacheStoreService>();
        var formattedKey = $"{TestCacheStoreId}-mykey";
        var returnValue = new byte[] { 1, 2, 3 };

        locator.Setup(l => l.GetCacheStoreProxy(formattedKey, It.IsAny<CancellationToken>())).ReturnsAsync(proxy.Object);
        proxy.Setup(p => p.GetCachedItemAsync(formattedKey)).ReturnsAsync(returnValue);

        var cache = CreateCache(locator.Object, timeProvider);

        var (listener, recordings) = CreateListener();
        using (listener)
        {
            var result = await cache.GetAsync("mykey", TestContext.Current.CancellationToken);

            Assert.NotNull(result);

            // cache.client.gets with result=hit
            var hit = recordings.Find(r =>
                r.Name == "cache.client.gets" && HasTag(r.Tags, "result", "hit"));
            Assert.NotEqual(default, hit);
            Assert.Equal(1L, (long)hit.Value);

            // cache.client.operation.duration with operation=get and status=success
            Assert.Contains(recordings, r =>
                r.Name == "cache.client.operation.duration"
                && HasTag(r.Tags, "operation", "get")
                && HasTag(r.Tags, "status", "success"));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 12 — GetAsync: proxy returns null → records miss
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task GetAsync_ProxyReturnsNull_RecordsMiss(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var proxy = new Mock<ICacheStoreService>();
        var formattedKey = $"{TestCacheStoreId}-mykey";

        locator.Setup(l => l.GetCacheStoreProxy(formattedKey, It.IsAny<CancellationToken>())).ReturnsAsync(proxy.Object);
        proxy.Setup(p => p.GetCachedItemAsync(formattedKey)).ReturnsAsync((byte[]?)null);

        var cache = CreateCache(locator.Object, timeProvider);

        var (listener, recordings) = CreateListener();
        using (listener)
        {
            var result = await cache.GetAsync("mykey", TestContext.Current.CancellationToken);

            Assert.Null(result);

            var miss = recordings.Find(r =>
                r.Name == "cache.client.gets" && HasTag(r.Tags, "result", "miss"));
            Assert.NotEqual(default, miss);
            Assert.Equal(1L, (long)miss.Value);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 13 — GetAsync: proxy throws → records error status, exception rethrown
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task GetAsync_ProxyThrows_RecordsErrorStatus(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var proxy = new Mock<ICacheStoreService>();
        var formattedKey = $"{TestCacheStoreId}-mykey";

        locator.Setup(l => l.GetCacheStoreProxy(formattedKey, It.IsAny<CancellationToken>())).ReturnsAsync(proxy.Object);
        proxy.Setup(p => p.GetCachedItemAsync(formattedKey)).ThrowsAsync(new InvalidOperationException("store error"));

        var cache = CreateCache(locator.Object, timeProvider);

        var (listener, recordings) = CreateListener();
        using (listener)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => cache.GetAsync("mykey", TestContext.Current.CancellationToken));

            // Duration must be recorded with status=error even though exception was thrown
            Assert.Contains(recordings, r =>
                r.Name == "cache.client.operation.duration"
                && HasTag(r.Tags, "operation", "get")
                && HasTag(r.Tags, "status", "error"));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 14 — SetAsync: valid value → records value size and duration
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task SetAsync_ValidValue_RecordsValueSizeAndDuration(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var proxy = new Mock<ICacheStoreService>();
        var payload = new byte[] { 10, 20, 30, 40, 50 }; // 5 bytes
        var formattedKey = $"{TestCacheStoreId}-setkey";

        locator.Setup(l => l.GetCacheStoreProxy(formattedKey, It.IsAny<CancellationToken>())).ReturnsAsync(proxy.Object);

        var cache = CreateCache(locator.Object, timeProvider);

        var (listener, recordings) = CreateListener();
        using (listener)
        {
            await cache.SetAsync(
                "setkey",
                payload,
                new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromSeconds(30) },
                TestContext.Current.CancellationToken);

            // cache.client.value.size records the payload length
            var sizeRec = recordings.Find(r => r.Name == "cache.client.value.size");
            Assert.NotEqual(default, sizeRec);
            Assert.Equal(5L, (long)sizeRec.Value);

            // cache.client.operation.duration with operation=set and status=success
            Assert.Contains(recordings, r =>
                r.Name == "cache.client.operation.duration"
                && HasTag(r.Tags, "operation", "set")
                && HasTag(r.Tags, "status", "success"));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 15 — SetAsync: no expiration provided → records default expiration applied
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task SetAsync_NoExpirationProvided_RecordsDefaultExpirationApplied(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var proxy = new Mock<ICacheStoreService>();
        var formattedKey = $"{TestCacheStoreId}-defkey";
        locator.Setup(l => l.GetCacheStoreProxy(formattedKey, It.IsAny<CancellationToken>())).ReturnsAsync(proxy.Object);

        // DefaultSlidingExpiration = 60s is set by CreateCache's default
        var cache = CreateCache(locator.Object, timeProvider);

        var (listener, recordings) = CreateListener();
        using (listener)
        {
            await cache.SetAsync(
                "defkey",
                new byte[] { 1 },
                new DistributedCacheEntryOptions(), // no expiration
                TestContext.Current.CancellationToken);

            var defaultApplied = recordings.Find(r =>
                r.Name == "cache.client.default_expiration_applied");
            Assert.NotEqual(default, defaultApplied);
            Assert.Equal(1L, (long)defaultApplied.Value);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 16 — SetAsync: explicit expiration provided → does NOT record default applied
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task SetAsync_ExpirationProvided_DoesNotRecordDefaultExpirationApplied(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var proxy = new Mock<ICacheStoreService>();
        var formattedKey = $"{TestCacheStoreId}-expkey";
        locator.Setup(l => l.GetCacheStoreProxy(formattedKey, It.IsAny<CancellationToken>())).ReturnsAsync(proxy.Object);

        var cache = CreateCache(locator.Object, timeProvider);

        var (listener, recordings) = CreateListener();
        using (listener)
        {
            await cache.SetAsync(
                "expkey",
                new byte[] { 1 },
                new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(5) },
                TestContext.Current.CancellationToken);

            Assert.DoesNotContain(recordings, r =>
                r.Name == "cache.client.default_expiration_applied");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 17 — RemoveAsync: valid key → records operation duration
    // ──────────────────────────────────────────────────────────────────────────

    [Theory, AutoMoqData]
    public async Task RemoveAsync_ValidKey_RecordsOperationDuration(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var proxy = new Mock<ICacheStoreService>();
        var formattedKey = $"{TestCacheStoreId}-rmkey";
        locator.Setup(l => l.GetCacheStoreProxy(formattedKey, It.IsAny<CancellationToken>())).ReturnsAsync(proxy.Object);

        var cache = CreateCache(locator.Object, timeProvider);

        var (listener, recordings) = CreateListener();
        using (listener)
        {
            await cache.RemoveAsync("rmkey", TestContext.Current.CancellationToken);

            Assert.Contains(recordings, r =>
                r.Name == "cache.client.operation.duration"
                && HasTag(r.Tags, "operation", "remove")
                && HasTag(r.Tags, "status", "success"));
        }
    }
}
