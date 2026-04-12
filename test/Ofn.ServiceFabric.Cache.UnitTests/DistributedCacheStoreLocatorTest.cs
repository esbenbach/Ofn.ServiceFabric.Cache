namespace Ofn.ServiceFabric.Cache.UnitTests;

using System.Fabric;
using System.Fabric.Query;
using System.Reflection;
using Microsoft.Extensions.Options;
using Microsoft.ServiceFabric.Services.Client;
using Moq;
using Ofn.ServiceFabric.Cache.Abstractions;
using Ofn.ServiceFabric.Cache.Client;
using Xunit;

public class DistributedCacheStoreLocatorTest
{
    // Note: FabricClient is sealed and new'd directly in DistributedCacheStoreLocator.
    // FetchPartitionListAsync and CreateCacheStoreProxy are exposed as protected internal
    // virtual methods so that tests can override them without a real Service Fabric cluster.

    [Fact]
    public async Task GetCacheStoreProxy_CalledConcurrently_PartitionListFetchedOnlyOnce()
    {
        var partitionList = CreatePartitionList([(Guid.NewGuid(), long.MinValue, long.MaxValue)]);

        var fetchCallCount = 0;
        var fetchStarted = new SemaphoreSlim(0, 1);
        var fetchCanComplete = new TaskCompletionSource();

        var locator = new TestableLocator(
            Options.Create(new ServiceFabricCacheOptions { CacheStoreServiceUri = new Uri("fabric:/test/cache") }),
            async _ =>
            {
                Interlocked.Increment(ref fetchCallCount);
                fetchStarted.Release();         // notify that the first fetch is in flight
                await fetchCanComplete.Task;    // block until the test allows completion
                return partitionList;
            });

        // Start task 1 — it will acquire the semaphore and block inside FetchPartitionListAsync
        var task1 = Task.Run(() => locator.GetCacheStoreProxy("key1"));
        await fetchStarted.WaitAsync(TestContext.Current.CancellationToken);

        // Start concurrent tasks while task1 holds _partitionListLock
        var concurrentTasks = Enumerable.Range(0, 5)
            .Select(_ => Task.Run(() => locator.GetCacheStoreProxy("key1")))
            .ToArray();

        // Give the concurrent tasks time to reach _partitionListLock.WaitAsync()
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Allow the first fetch to complete — all waiting tasks should reuse the result
        fetchCanComplete.SetResult();
        await Task.WhenAll(concurrentTasks.Prepend(task1));

        Assert.Equal(1, fetchCallCount);
    }

    [Fact]
    public async Task GetCacheStoreProxy_ValidKey_ReturnsProxy()
    {
        var partitionId = Guid.NewGuid();
        var partitionList = CreatePartitionList([(partitionId, long.MinValue, long.MaxValue)]);
        var expectedProxy = Mock.Of<ICacheStoreService>();

        var locator = new TestableLocator(
            Options.Create(new ServiceFabricCacheOptions { CacheStoreServiceUri = new Uri("fabric:/test/cache") }),
            _ => Task.FromResult(partitionList),
            (_, _) => expectedProxy);

        var result = await locator.GetCacheStoreProxy("anyKey");

        Assert.Same(expectedProxy, result);
    }

    [Fact]
    public async Task GetCacheStoreProxy_NoMatchingPartition_ThrowsMeaningfulException()
    {
        // Partition covers only [1, 1]; MD5 hash of "anyKey" will not land on exactly 1
        var partitionList = CreatePartitionList([(Guid.NewGuid(), 1L, 1L)]);

        var locator = new TestableLocator(
            Options.Create(new ServiceFabricCacheOptions { CacheStoreServiceUri = new Uri("fabric:/test/cache") }),
            _ => Task.FromResult(partitionList));

        await Assert.ThrowsAsync<InvalidOperationException>(() => locator.GetCacheStoreProxy("anyKey"));
    }

    // Creates a ServicePartitionList containing StatefulServicePartitions with Int64 range info.
    // Both StatefulServicePartition and Int64RangePartitionInformation only expose internal/reference-assembly
    // constructors and setters, so reflection is required to construct test instances.
    private static ServicePartitionList CreatePartitionList(IEnumerable<(Guid id, long low, long high)> ranges)
    {
        var int64InfoType = typeof(System.Fabric.Int64RangePartitionInformation);
        var partitionBaseType = typeof(System.Fabric.Query.Partition);
        var statefulPartitionType = typeof(StatefulServicePartition);

        var list = new ServicePartitionList();
        foreach (var (id, low, high) in ranges)
        {
            // Create Int64RangePartitionInformation via its runtime-only public ctor
            var partInfo = (System.Fabric.ServicePartitionInformation)Activator.CreateInstance(
                int64InfoType,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, [], null)!;

            SetField(int64InfoType, partInfo, "<LowKey>k__BackingField", low);
            SetField(int64InfoType, partInfo, "<HighKey>k__BackingField", high);
            SetField(typeof(System.Fabric.ServicePartitionInformation), partInfo, "<Id>k__BackingField", id);

            // Create StatefulServicePartition and set its inherited PartitionInformation
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
            var field = declaringType.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!;
            field.SetValue(target, value);
        }
    }

    private sealed class TestableLocator : DistributedCacheStoreLocator
    {
        private readonly Func<Uri, Task<ServicePartitionList>> _fetchPartitions;
        private readonly Func<Uri, ServicePartitionKey, ICacheStoreService> _createProxy;

        public TestableLocator(
            IOptions<ServiceFabricCacheOptions> options,
            Func<Uri, Task<ServicePartitionList>> fetchPartitions,
            Func<Uri, ServicePartitionKey, ICacheStoreService>? createProxy = null)
            : base(options)
        {
            _fetchPartitions = fetchPartitions;
            _createProxy = createProxy ?? ((_, _) => Mock.Of<ICacheStoreService>());
        }

        protected override Task<ServicePartitionList> FetchPartitionListAsync(Uri uri)
            => _fetchPartitions(uri);

        protected override ICacheStoreService CreateCacheStoreProxy(Uri uri, ServicePartitionKey partitionKey, string endpoint)
            => _createProxy(uri, partitionKey);
    }
}
