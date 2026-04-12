namespace Ofn.ServiceFabric.Cache.UnitTests;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Fabric;
using System.Fabric.Query;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ServiceFabric.Services.Client;
using Moq;
using Ofn.ServiceFabric.Cache.Abstractions;
using Ofn.ServiceFabric.Cache.Client;
using Xunit;

/// <summary>
/// Tests that <see cref="DistributedCacheStoreLocator"/> emits the expected
/// System.Diagnostics.Metrics measurements for service discovery and partition-list refresh.
/// </summary>
public class DistributedCacheStoreLocatorMetricsTest
{
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
    // Tag assertion helper
    // ──────────────────────────────────────────────────────────────────────────

    private static bool HasTag(KeyValuePair<string, object?>[] tags, string key, string value) =>
        Array.Exists(tags, t => t.Key == key && (string?)t.Value == value);

    // ──────────────────────────────────────────────────────────────────────────
    // Partition list factory (mirrors DistributedCacheStoreLocatorTest.CreatePartitionList)
    // ──────────────────────────────────────────────────────────────────────────

    private static ServicePartitionList CreatePartitionList(
        IEnumerable<(Guid id, long low, long high)> ranges)
    {
        var int64InfoType = typeof(Int64RangePartitionInformation);
        var partitionBaseType = typeof(Partition);
        var statefulPartitionType = typeof(StatefulServicePartition);

        var list = new ServicePartitionList();
        foreach (var (id, low, high) in ranges)
        {
            var partInfo = (ServicePartitionInformation)Activator.CreateInstance(
                int64InfoType,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, [], null)!;

            SetField(int64InfoType, partInfo, "<LowKey>k__BackingField", low);
            SetField(int64InfoType, partInfo, "<HighKey>k__BackingField", high);
            SetField(typeof(ServicePartitionInformation), partInfo, "<Id>k__BackingField", id);

            var partition = (Partition)Activator.CreateInstance(
                statefulPartitionType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                null, [], null)!;

            SetField(partitionBaseType, partition, "<PartitionInformation>k__BackingField", partInfo);
            list.Add(partition);
        }

        return list;

        static void SetField(Type declaringType, object target, string fieldName, object value)
        {
            var field = declaringType.GetField(
                fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!;
            field.SetValue(target, value);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TestableLocator — allows overriding discovery and partition-list fetch
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class TestableLocator : DistributedCacheStoreLocator
    {
        private readonly Func<Task<Uri?>> _locateCacheStore;
        private readonly Func<Uri, Task<ServicePartitionList>>? _fetchPartitions;
        private readonly Func<Uri, ServicePartitionKey, ICacheStoreService>? _createProxy;

        public TestableLocator(
            IOptions<ServiceFabricCacheOptions> options,
            Func<Task<Uri?>> locateCacheStore,
            Func<Uri, Task<ServicePartitionList>>? fetchPartitions = null,
            Func<Uri, ServicePartitionKey, ICacheStoreService>? createProxy = null)
            : base(options, NullLogger<DistributedCacheStoreLocator>.Instance)
        {
            _locateCacheStore = locateCacheStore;
            _fetchPartitions = fetchPartitions;
            _createProxy = createProxy;
        }

        protected override Task<Uri?> LocateCacheStoreAsync(CancellationToken cancellationToken = default) =>
            _locateCacheStore();

        protected override Task<ServicePartitionList> FetchPartitionListAsync(Uri uri, CancellationToken cancellationToken = default) =>
            _fetchPartitions != null
                ? _fetchPartitions(uri)
                : Task.FromResult(CreatePartitionList([(Guid.NewGuid(), long.MinValue, long.MaxValue)]));

        protected override ICacheStoreService CreateCacheStoreProxy(
            Uri uri, ServicePartitionKey partitionKey, string endpoint) =>
            _createProxy != null ? _createProxy(uri, partitionKey) : Mock.Of<ICacheStoreService>();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 18 — Discovery succeeds → records discovery duration with status=success
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCacheStoreProxy_ServiceDiscoverySucceeds_RecordsDiscoveryDuration()
    {
        var discoveredUri = new Uri("fabric:/test/cache");
        var partitionList = CreatePartitionList([(Guid.NewGuid(), long.MinValue, long.MaxValue)]);

        // CacheStoreServiceUri = null so that auto-discovery runs
        var locator = new TestableLocator(
            Options.Create(new ServiceFabricCacheOptions { CacheStoreServiceUri = null! }),
            locateCacheStore: () => Task.FromResult<Uri?>(discoveredUri),
            fetchPartitions: _ => Task.FromResult(partitionList));

        var (listener, recordings) = CreateListener();
        using (listener)
        {
            await locator.GetCacheStoreProxy("anyKey");

            // cache.client.discovery.duration with status=success
            Assert.Contains(recordings, r =>
                r.Name == "cache.client.discovery.duration"
                && HasTag(r.Tags, "status", "success"));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 19 — Discovery fails (null returned) → records failure counter and duration
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCacheStoreProxy_ServiceDiscoveryFails_RecordsDiscoveryFailureAndDuration()
    {
        // CacheStoreServiceUri = null so that auto-discovery runs; locator returns null (not found)
        var locator = new TestableLocator(
            Options.Create(new ServiceFabricCacheOptions { CacheStoreServiceUri = null! }),
            locateCacheStore: () => Task.FromResult<Uri?>(null));

        var (listener, recordings) = CreateListener();
        using (listener)
        {
            await Assert.ThrowsAsync<CacheStoreNotFoundException>(
                () => locator.GetCacheStoreProxy("anyKey"));

            // cache.client.discovery.failures counter incremented
            var failure = recordings.Find(r => r.Name == "cache.client.discovery.failures");
            Assert.NotEqual(default, failure);
            Assert.Equal(1L, (long)failure.Value);

            // cache.client.discovery.duration with status=failed
            Assert.Contains(recordings, r =>
                r.Name == "cache.client.discovery.duration"
                && HasTag(r.Tags, "status", "failed"));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 20 — Partition list fetch on first call → records refresh duration
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCacheStoreProxy_PartitionListFetch_RecordsRefreshDuration()
    {
        var serviceUri = new Uri("fabric:/test/cache");
        var partitionList = CreatePartitionList([(Guid.NewGuid(), long.MinValue, long.MaxValue)]);

        // serviceUri is pre-configured so the discovery block is skipped entirely;
        // _partitionList starts null so FetchPartitionListAsync is called exactly once.
        var locator = new TestableLocator(
            Options.Create(new ServiceFabricCacheOptions { CacheStoreServiceUri = serviceUri }),
            locateCacheStore: () => Task.FromResult<Uri?>(serviceUri), // not reached
            fetchPartitions: _ => Task.FromResult(partitionList));

        var (listener, recordings) = CreateListener();
        using (listener)
        {
            await locator.GetCacheStoreProxy("anyKey");

            // cache.client.partition_list.refresh.duration must be recorded
            Assert.Contains(recordings, r =>
                r.Name == "cache.client.partition_list.refresh.duration");
        }
    }
}
