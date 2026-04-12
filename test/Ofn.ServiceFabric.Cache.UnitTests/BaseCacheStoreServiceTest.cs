namespace Ofn.ServiceFabric.Cache.UnitTests;

using System.Collections.Generic;
using System.Fabric;
using System.Text;
using AutoFixture.Xunit3;
using Microsoft.Extensions.Logging;
using Microsoft.ServiceFabric.Data;
using Microsoft.ServiceFabric.Data.Collections;
using Moq;
using Ofn.ServiceFabric.Cache.Abstractions;
using Xunit;

public class BaseCacheStoreServiceTest
{
    [Theory, AutoMoqData]
    public async Task GetCachedItemAsync_GetItemThatExistsWithSlidingExpiration_ItemIsMovedToLastItem(
        [Frozen]Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen]Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen]Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen]FakeTimeProvider timeProvider,
        [Greedy]StubCacheStoreService cacheStore)
    {
        var cacheValue = Encoding.UTF8.GetBytes("someValue");
        var currentTime = new DateTime(2019, 2, 1, 1, 0, 0);

        timeProvider.SetUtcNow(currentTime);

        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        var metadata = SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        await cacheStore.SetCachedItemAsync("mykey1", cacheValue, TimeSpan.FromSeconds(10), null);
        await cacheStore.SetCachedItemAsync("mykey2", cacheValue, TimeSpan.FromSeconds(10), null);
        await cacheStore.SetCachedItemAsync("mykey3", cacheValue, TimeSpan.FromSeconds(10), null);

        Assert.Equal("mykey3", metadata["CacheStoreMetadata"].LastCacheKey);

        await cacheStore.GetCachedItemAsync("mykey2");

        Assert.Equal("mykey2", metadata["CacheStoreMetadata"].LastCacheKey);
    }

    [Theory, AutoMoqData]
    public async Task GetCachedItemAsync_GetItemThatExistsWithAbsoluteExpiration_ItemIsMovedToLastItem(
        [Frozen]Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen]Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen]Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen]FakeTimeProvider timeProvider,
        [Greedy]StubCacheStoreService cacheStore)
    {
        var cacheValue = Encoding.UTF8.GetBytes("someValue");
        var currentTime = new DateTime(2019, 2, 1, 1, 0, 0);
        var expireTime = currentTime.AddSeconds(30);

        timeProvider.SetUtcNow(currentTime);

        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        var metadata = SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        await cacheStore.SetCachedItemAsync("mykey1", cacheValue, null, expireTime);
        await cacheStore.SetCachedItemAsync("mykey2", cacheValue, null, expireTime);
        await cacheStore.SetCachedItemAsync("mykey3", cacheValue, null, expireTime);

        Assert.Equal("mykey3", metadata["CacheStoreMetadata"].LastCacheKey);

        await cacheStore.GetCachedItemAsync("mykey2");

        Assert.Equal("mykey2", metadata["CacheStoreMetadata"].LastCacheKey);
    }

    [Theory, AutoMoqData]
    public async Task GetCachedItemAsync_GetItemThatDoesNotExist_NullResultReturned(
        [Frozen]Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen]Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen]Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen]FakeTimeProvider timeProvider,
        [Greedy]StubCacheStoreService cacheStore)
    {
        var cacheValue = Encoding.UTF8.GetBytes("someValue");
        var currentTime = new DateTime(2019, 2, 1, 1, 0, 0);
        var expireTime = currentTime.AddSeconds(1);

        timeProvider.SetUtcNow(currentTime);

        SetupInMemoryStores(stateManager, metadataDict, cacheStore);
        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);

        var result = await cacheStore.GetCachedItemAsync("keyThatDoesNotExist");
        Assert.Null(result);
    }

    [Theory, AutoMoqData]
    public async Task GetCachedItemAsync_GetItemThatDoesHaveKeyAndIsIsNotAbsoluteExpired_CachedItemReturned(
        [Frozen]Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen]Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen]Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen]FakeTimeProvider timeProvider,
        [Greedy]StubCacheStoreService cacheStore)
    {
        var cacheValue = Encoding.UTF8.GetBytes("someValue");
        var currentTime = new DateTime(2019, 2, 1, 1, 0, 0);
        var expireTime = currentTime.AddSeconds(1);

        timeProvider.SetUtcNow(currentTime);

        SetupInMemoryStores(stateManager, metadataDict, cacheStore);
        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);

        await cacheStore.SetCachedItemAsync("mykey", cacheValue, null, expireTime);
        var result = await cacheStore.GetCachedItemAsync("mykey");
        Assert.Equal(cacheValue, result);
    }

    [Theory, AutoMoqData]
    public async Task GetCachedItemAsync_GetItemThatDoesHaveKeyAndIsIsAbsoluteExpired_NullResultReturned(
        [Frozen]Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen]Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen]Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen]FakeTimeProvider timeProvider,
        [Greedy]StubCacheStoreService cacheStore)
    {
        var cacheValue = Encoding.UTF8.GetBytes("someValue");
        var currentTime = new DateTime(2019, 2, 1, 1, 0, 0);
        var expireTime = currentTime.AddSeconds(-1);

        timeProvider.SetUtcNow(currentTime);

        SetupInMemoryStores(stateManager, metadataDict, cacheStore);
        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);

        await cacheStore.SetCachedItemAsync("mykey", cacheValue, null, expireTime);
        var result = await cacheStore.GetCachedItemAsync("mykey");
        Assert.Null(result);
    }

    [Theory, AutoMoqData]
    public async Task GetCachedItemAsync_GetItemThatDoesHaveKeyAndIsIsAbsoluteExpiredDoesNotSlideTime_ExpireTimeDoesNotSlide(
        [Frozen]Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen]Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen]Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen]FakeTimeProvider timeProvider,
        [Greedy]StubCacheStoreService cacheStore)
    {
        var cacheValue = Encoding.UTF8.GetBytes("someValue");
        var currentTime = new DateTime(2019, 2, 1, 1, 0, 0);
        var expireTime = currentTime.AddSeconds(5);

        timeProvider.SetUtcNow(currentTime);

        SetupInMemoryStores(stateManager, metadataDict, cacheStore);
        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);

        await cacheStore.SetCachedItemAsync("mykey", cacheValue, null, expireTime);
        var result = await cacheStore.GetCachedItemAsync("mykey");
        Assert.Equal(cacheValue, result);

        timeProvider.SetUtcNow(currentTime.AddSeconds(5));

        var resultAfter6Seconds = await cacheStore.GetCachedItemAsync("mykey");
        Assert.Null(resultAfter6Seconds);
    }

    [Theory, AutoMoqData]
    public async Task GetCachedItemAsync_GetItemThatDoesHaveKeyAndIsIsNotSlidingExpired_CachedItemReturned(
        [Frozen]Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen]Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen]Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen]FakeTimeProvider timeProvider,
        [Greedy]StubCacheStoreService cacheStore)
    {
        var cacheValue = Encoding.UTF8.GetBytes("someValue");
        var currentTime = new DateTime(2019, 2, 1, 1, 0, 0);

        timeProvider.SetUtcNow(currentTime);

        SetupInMemoryStores(stateManager, metadataDict, cacheStore);
        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);

        await cacheStore.SetCachedItemAsync("mykey", cacheValue, TimeSpan.FromSeconds(1), null);
        var result = await cacheStore.GetCachedItemAsync("mykey");
        Assert.Equal(cacheValue, result);
    }


    [Theory, AutoMoqData]
    public async Task GetCachedItemAsync_GetItemThatDoesHaveKeyAndIsIsSlidingExpired_NullResultReturned(
        [Frozen]Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen]Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen]Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen]FakeTimeProvider timeProvider,
        [Greedy]StubCacheStoreService cacheStore)
    {
        var cacheValue = Encoding.UTF8.GetBytes("someValue");
        var currentTime = new DateTime(2019, 2, 1, 1, 0, 0);

        timeProvider.SetUtcNow(currentTime);

        SetupInMemoryStores(stateManager, metadataDict, cacheStore);
        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);

        await cacheStore.SetCachedItemAsync("mykey", cacheValue, TimeSpan.FromSeconds(1), null);
        timeProvider.SetUtcNow(currentTime.AddSeconds(2));
        var result = await cacheStore.GetCachedItemAsync("mykey");
        Assert.Null(result);
    }

    [Theory, AutoMoqData]
    public async Task GetCachedItemAsync_GetItemThatDoesHaveKeyAndIsIsSlidingExpired_SlidedExpirationUpdates(
        [Frozen]Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen]Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen]Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen]FakeTimeProvider timeProvider,
        [Greedy]StubCacheStoreService cacheStore)
    {
        var cacheValue = Encoding.UTF8.GetBytes("someValue");
        var currentTime = new DateTime(2019, 2, 1, 1, 0, 0);

        timeProvider.SetUtcNow(currentTime);

        SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        await cacheStore.SetCachedItemAsync("mykey", cacheValue, TimeSpan.FromSeconds(10), null);
        timeProvider.SetUtcNow(currentTime.AddSeconds(5));
        var resultAfter5Seconds = await cacheStore.GetCachedItemAsync("mykey");
        Assert.Equal(cacheValue, resultAfter5Seconds);
        timeProvider.SetUtcNow(currentTime.AddSeconds(8));
        var resultAfter8Seconds = await cacheStore.GetCachedItemAsync("mykey");
        Assert.Equal(cacheValue, resultAfter8Seconds);
        timeProvider.SetUtcNow(currentTime.AddSeconds(9));
        var resultAfter9Seconds = await cacheStore.GetCachedItemAsync("mykey");
        Assert.Equal(cacheValue, resultAfter9Seconds);
        timeProvider.SetUtcNow(currentTime.AddSeconds(19));
        var resultAfter19Seconds = await cacheStore.GetCachedItemAsync("mykey");
        Assert.Null(resultAfter19Seconds);
    }

    [Theory, AutoMoqData]
    public async Task SetCachedItemAsync_AddItemsToCreateLinkedDictionary_DictionaryCreatedWithItemsLinked(
        [Frozen]Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen]Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen]Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen]FakeTimeProvider timeProvider,
        [Greedy]StubCacheStoreService cacheStore)
    {
        var cacheValue = Encoding.UTF8.GetBytes("someValue");
        var currentTime = new DateTime(2019, 2, 1, 1, 0, 0);

        timeProvider.SetUtcNow(currentTime);

        var cachedItems = SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        var metadata = SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        await cacheStore.SetCachedItemAsync("1", cacheValue, TimeSpan.FromSeconds(10), null);
        await cacheStore.SetCachedItemAsync("2", cacheValue, TimeSpan.FromSeconds(10), null);
        await cacheStore.SetCachedItemAsync("3", cacheValue, TimeSpan.FromSeconds(10), null);
        await cacheStore.SetCachedItemAsync("4", cacheValue, TimeSpan.FromSeconds(10), null);

        Assert.Null(cachedItems["1"].BeforeCacheKey);
        foreach (var item in cachedItems)
        {
            if (item.Value.BeforeCacheKey != null)
            {
                Assert.Equal(item.Key, cachedItems[item.Value.BeforeCacheKey].AfterCacheKey);
            }
            if (item.Value.AfterCacheKey != null)
            {
                Assert.Equal(item.Key, cachedItems[item.Value.AfterCacheKey].BeforeCacheKey);
            }
        }
        Assert.Null(cachedItems["4"].AfterCacheKey);

        Assert.Equal("1", metadata["CacheStoreMetadata"].FirstCacheKey);
        Assert.Equal("4", metadata["CacheStoreMetadata"].LastCacheKey);
        Assert.Equal((cacheValue.Length + 250) * cachedItems.Count, metadata["CacheStoreMetadata"].Size);
    }

    [Theory, AutoMoqData]
    public async Task RemoveCachedItemAsync_RemoveItemsFromLinkedDictionary_ListStaysLinkedTogetherAfterItemsRemoved(
        [Frozen]Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen]Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen]Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen]FakeTimeProvider timeProvider,
        [Greedy]StubCacheStoreService cacheStore)
    {
        var cacheValue = Encoding.UTF8.GetBytes("someValue");
        var currentTime = new DateTime(2019, 2, 1, 1, 0, 0);

        timeProvider.SetUtcNow(currentTime);

        var cachedItems = SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        var metadata = SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        await cacheStore.SetCachedItemAsync("1", cacheValue, TimeSpan.FromSeconds(10), null);
        await cacheStore.SetCachedItemAsync("2", cacheValue, TimeSpan.FromSeconds(10), null);
        await cacheStore.SetCachedItemAsync("3", cacheValue, TimeSpan.FromSeconds(10), null);
        await cacheStore.SetCachedItemAsync("4", cacheValue, TimeSpan.FromSeconds(10), null);
        await cacheStore.SetCachedItemAsync("5", cacheValue, TimeSpan.FromSeconds(10), null);
        await cacheStore.SetCachedItemAsync("6", cacheValue, TimeSpan.FromSeconds(10), null);
        await cacheStore.SetCachedItemAsync("7", cacheValue, TimeSpan.FromSeconds(10), null);
        await cacheStore.SetCachedItemAsync("8", cacheValue, TimeSpan.FromSeconds(10), null);

        await cacheStore.RemoveCachedItemAsync("3");
        await cacheStore.RemoveCachedItemAsync("4");
        await cacheStore.RemoveCachedItemAsync("8");
        await cacheStore.RemoveCachedItemAsync("1");

        Assert.Null(cachedItems["2"].BeforeCacheKey);
        foreach (var item in cachedItems)
        {
            if (item.Value.BeforeCacheKey != null)
            {
                Assert.Equal(item.Key, cachedItems[item.Value.BeforeCacheKey].AfterCacheKey);
            }
            if (item.Value.AfterCacheKey != null)
            {
                Assert.Equal(item.Key, cachedItems[item.Value.AfterCacheKey].BeforeCacheKey);
            }
        }
        Assert.Null(cachedItems["7"].AfterCacheKey);

        Assert.Equal("2", metadata["CacheStoreMetadata"].FirstCacheKey);
        Assert.Equal("7", metadata["CacheStoreMetadata"].LastCacheKey);
        Assert.Equal((cacheValue.Length + 250) * cachedItems.Count, metadata["CacheStoreMetadata"].Size);
    }

    [Theory, AutoMoqData]
    public async Task RemoveLeastRecentlyUsedCacheItemWhenOverMaxCacheSize_RemoveItemsFromLinkedDictionary_DoesNotRemoveNonExpiredItems(
        [Frozen]Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen]Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen]Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen]FakeTimeProvider timeProvider,
        [Greedy]StubCacheStoreService cacheStore)
    {
        var cacheValue = new byte[1000000];
        var currentTime = new DateTime(2019, 2, 1, 1, 0, 0);

        timeProvider.SetUtcNow(currentTime);

        var cachedItems = SetupInMemoryStores(stateManager, cacheItemDict, cacheStore);
        var metadata = SetupInMemoryStores(stateManager, metadataDict, cacheStore);

        await cacheStore.SetCachedItemAsync("1", cacheValue, TimeSpan.FromMinutes(10), null);
        for (var i = 2; i <= 10; i++)
        {
            await cacheStore.SetCachedItemAsync(i.ToString(), cacheValue, TimeSpan.FromSeconds(10), null);
        }

        timeProvider.SetUtcNow(currentTime.AddSeconds(10));
        await cacheStore.RemoveLeastRecentlyUsedCacheItemWhenOverMaxSize();

        Assert.Single(cachedItems);
        Assert.Equal("1", metadata["CacheStoreMetadata"].FirstCacheKey);
        Assert.Equal("1", metadata["CacheStoreMetadata"].LastCacheKey);
    }


    [Theory, AutoMoqData]
    public async Task RunAsync_PruningThrowsTransientException_ExceptionIsLoggedAndLoopContinues(
        [Frozen]Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen]Mock<ILogger<ICacheStoreService>> logger,
        [Greedy]StubCacheStoreService cacheStore)
    {
        // _cacheStore is not initialized (no SetupInMemoryStores call), so the CacheStore
        // property throws InvalidOperationException on the first pruning attempt,
        // which is caught by the loop, logged, and the loop continues.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cacheStore.RunAsyncPublic(cts.Token));

        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<InvalidOperationException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Theory, AutoMoqData]
    public async Task RunAsync_CancellationRequestedImmediately_TerminatesWithoutError(
        [Greedy]StubCacheStoreService cacheStore)
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Record.ExceptionAsync(() => cacheStore.RunAsyncPublic(cts.Token));

        Assert.True(ex is null || ex is OperationCanceledException);
    }

    [Fact]
    public void SetCachedItemAsync_SizeExceeds2GBThreshold_DoesNotOverflow()
    {
        long sizeOver2GB = (long)int.MaxValue + 1;
        var metadata = new CacheStoreMetadata(sizeOver2GB, "firstKey", "lastKey");
        Assert.Equal(sizeOver2GB, metadata.Size);
    }

    [Theory, AutoMoqData]
    public async Task RemoveCachedItemAsync_CustomByteSizeOffset_SizeAccountsForOffset(
        [Frozen]Mock<IReliableStateManagerReplica2> stateManager,
        [Frozen]Mock<IReliableDictionary<string, CachedItem>> cacheItemDict,
        [Frozen]Mock<IReliableDictionary<string, CacheStoreMetadata>> metadataDict,
        [Frozen]FakeTimeProvider timeProvider,
        StatefulServiceContext context)
    {
        var customSettings = new CacheStoreSettings { MaxCacheSize = 1, CachePruningInterval = 0, ByteSizeOffset = 100 };
        var cacheStore = new CustomSettingsStub(context, customSettings, stateManager.Object, timeProvider);

        var cacheValue = Encoding.UTF8.GetBytes("someValue");
        var currentTime = new DateTime(2019, 2, 1, 1, 0, 0);

        timeProvider.SetUtcNow(currentTime);

        var cachedItems = SetupInMemoryStores(stateManager, cacheItemDict);
        var metadata = SetupInMemoryStores(stateManager, metadataDict);
        cacheStore.InitCacheStore(cacheItemDict.Object);
        cacheStore.InitCacheStoreMetadata(metadataDict.Object);

        await cacheStore.SetCachedItemAsync("key1", cacheValue, TimeSpan.FromSeconds(10), null);

        Assert.Equal(cacheValue.Length + 100, metadata["CacheStoreMetadata"].Size);
        Assert.NotEqual(cacheValue.Length + 250, metadata["CacheStoreMetadata"].Size);

        await cacheStore.RemoveCachedItemAsync("key1");

        Assert.Equal(0, metadata["CacheStoreMetadata"].Size);
    }

    private static Dictionary<TKey, TValue> SetupInMemoryStores<TKey, TValue>(Mock<IReliableStateManagerReplica2> stateManager, Mock<IReliableDictionary<TKey, TValue>> reliableDict, StubCacheStoreService? stub = null) where TKey : IComparable<TKey>, IEquatable<TKey>
    {
        var inMemoryDict = new Dictionary<TKey, TValue>();
        ConditionalValue<TValue> getItem(TKey key) => inMemoryDict.TryGetValue(key, out TValue? value) ? new ConditionalValue<TValue>(true, value) : new ConditionalValue<TValue>(false, default);

        stateManager.Setup(m => m.GetOrAddAsync<IReliableDictionary<TKey, TValue>>(It.IsAny<string>())).Returns(Task.FromResult(reliableDict.Object));
        reliableDict.Setup(m => m.TryGetValueAsync(It.IsAny<ITransaction>(), It.IsAny<TKey>())).Returns((ITransaction t, TKey key) => Task.FromResult(getItem(key)));
        reliableDict.Setup(m => m.TryGetValueAsync(It.IsAny<ITransaction>(), It.IsAny<TKey>(), It.IsAny<LockMode>())).Returns((ITransaction t, TKey key, LockMode l) => Task.FromResult(getItem(key)));
        reliableDict.Setup(m => m.SetAsync(It.IsAny<ITransaction>(), It.IsAny<TKey>(), It.IsAny<TValue>())).Returns((ITransaction t, TKey key, TValue ci) => { inMemoryDict[key] = ci; return Task.CompletedTask; });
        reliableDict.Setup(m => m.TryRemoveAsync(It.IsAny<ITransaction>(), It.IsAny<TKey>())).Returns((ITransaction t, TKey key) => { var r = getItem(key); inMemoryDict.Remove(key); return Task.FromResult(r); });

        if (stub is not null)
        {
            if (reliableDict.Object is IReliableDictionary<string, CachedItem> cacheItemStore)
                stub.InitCacheStore(cacheItemStore);
            else if (reliableDict.Object is IReliableDictionary<string, CacheStoreMetadata> metadataStore)
                stub.InitCacheStoreMetadata(metadataStore);
        }

        return inMemoryDict;
    }

    public class StubCacheStoreService : BaseCacheStoreService
    {
        public StubCacheStoreService(StatefulServiceContext context, IReliableStateManagerReplica2 replica, TimeProvider timeProvider, ILogger<ICacheStoreService>? logger = null)
            : base(context, new CacheStoreSettings() { MaxCacheSize = 1, CachePruningInterval = 0 }, replica, timeProvider, logger)
        {
        }

        public void InitCacheStore(IReliableDictionary<string, CachedItem> store) => _cacheStore = store;
        public void InitCacheStoreMetadata(IReliableDictionary<string, CacheStoreMetadata> store) => _cacheStoreMetadata = store;

        public Task RunAsyncPublic(CancellationToken cancellationToken) =>
            base.RunAsync(cancellationToken);

        public async Task RemoveLeastRecentlyUsedCacheItemWhenOverMaxSize()
        {
            await base.RemoveLeastRecentlyUsedCacheItemWhenOverMaxSize(CancellationToken.None);
        }
    }

    private class CustomSettingsStub : BaseCacheStoreService
    {
        public CustomSettingsStub(StatefulServiceContext context, CacheStoreSettings settings, IReliableStateManagerReplica2 replica, TimeProvider timeProvider)
            : base(context, settings, replica, timeProvider)
        {
        }

        public void InitCacheStore(IReliableDictionary<string, CachedItem> store) => _cacheStore = store;
        public void InitCacheStoreMetadata(IReliableDictionary<string, CacheStoreMetadata> store) => _cacheStoreMetadata = store;
    }
}
