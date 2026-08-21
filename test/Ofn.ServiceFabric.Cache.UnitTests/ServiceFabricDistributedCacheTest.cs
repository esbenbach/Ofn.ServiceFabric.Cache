namespace Ofn.ServiceFabric.Cache.UnitTests;

using AutoFixture.Xunit3;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Moq;
using Ofn.ServiceFabric.Cache.Abstractions;
using Ofn.ServiceFabric.Cache.Client;
using Xunit;

[Collection("Metrics")]
public class ServiceFabricDistributedCacheTest
{
    private static readonly Guid TestCacheStoreId = Guid.Parse("11111111-1111-1111-1111-111111111111");

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

    [Theory, AutoMoqData]
    public async Task GetAsync_ValidKey_CallsProxyWithFormattedKey(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var proxy = new Mock<ICacheStoreService>();
        var expectedKey = $"{TestCacheStoreId}-mykey";
        locator.Setup(l => l.GetCacheStoreProxy(expectedKey, It.IsAny<CancellationToken>())).ReturnsAsync(proxy.Object);

        var cache = CreateCache(locator.Object, timeProvider);
        await cache.GetAsync("mykey", TestContext.Current.CancellationToken);

        proxy.Verify(p => p.GetCachedItemAsync(expectedKey), Times.Once);
    }

    [Theory, AutoMoqData]
    public async Task SetAsync_NoExpirationProvided_DefaultSlidingExpirationApplied(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var proxy = new Mock<ICacheStoreService>();
        var formattedKey = $"{TestCacheStoreId}-mykey";
        locator.Setup(l => l.GetCacheStoreProxy(formattedKey, It.IsAny<CancellationToken>())).ReturnsAsync(proxy.Object);

        var cache = CreateCache(locator.Object, timeProvider);
        await cache.SetAsync("mykey", new byte[] { 1, 2, 3 }, new DistributedCacheEntryOptions(),
            TestContext.Current.CancellationToken);

        proxy.Verify(p => p.SetCachedItemAsync(
            formattedKey,
            It.IsAny<byte[]>(),
            TimeSpan.FromSeconds(60),
            null), Times.Once);
    }

    [Theory, AutoMoqData]
    public async Task SetAsync_SlidingExpirationProvided_ProvidedValuePassedToProxy(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var proxy = new Mock<ICacheStoreService>();
        var slidingExpiry = TimeSpan.FromMinutes(3);
        var formattedKey = $"{TestCacheStoreId}-mykey";
        locator.Setup(l => l.GetCacheStoreProxy(formattedKey, It.IsAny<CancellationToken>())).ReturnsAsync(proxy.Object);

        var cache = CreateCache(locator.Object, timeProvider);
        await cache.SetAsync("mykey", new byte[] { 1, 2, 3 }, new DistributedCacheEntryOptions
        {
            SlidingExpiration = slidingExpiry
        }, TestContext.Current.CancellationToken);

        proxy.Verify(p => p.SetCachedItemAsync(
            formattedKey,
            It.IsAny<byte[]>(),
            slidingExpiry,
            null), Times.Once);
    }

    [Theory, AutoMoqData]
    public async Task SetAsync_AbsoluteExpirationRelativeToNow_CalculatedCorrectly(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var now = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        timeProvider.SetUtcNow(now);

        var proxy = new Mock<ICacheStoreService>();
        var formattedKey = $"{TestCacheStoreId}-mykey";
        locator.Setup(l => l.GetCacheStoreProxy(formattedKey, It.IsAny<CancellationToken>())).ReturnsAsync(proxy.Object);

        var cache = CreateCache(locator.Object, timeProvider);
        await cache.SetAsync("mykey", new byte[] { 1, 2, 3 }, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
        }, TestContext.Current.CancellationToken);

        proxy.Verify(p => p.SetCachedItemAsync(
            formattedKey,
            It.IsAny<byte[]>(),
            null,
            now.AddMinutes(5)), Times.Once);
    }

    [Theory, AutoMoqData]
    public async Task RemoveAsync_ValidKey_CallsProxyRemove(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var proxy = new Mock<ICacheStoreService>();
        var formattedKey = $"{TestCacheStoreId}-mykey";
        locator.Setup(l => l.GetCacheStoreProxy(formattedKey, It.IsAny<CancellationToken>())).ReturnsAsync(proxy.Object);

        var cache = CreateCache(locator.Object, timeProvider);
        await cache.RemoveAsync("mykey", TestContext.Current.CancellationToken);

        proxy.Verify(p => p.RemoveCachedItemAsync(formattedKey), Times.Once);
    }

    [Theory, AutoMoqData]
    public async Task GetAsync_NullKey_ThrowsArgumentException(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var cache = CreateCache(locator.Object, timeProvider);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => cache.GetAsync(null!, TestContext.Current.CancellationToken));
    }

    [Theory, AutoMoqData]
    public async Task SetAsync_NullValue_ThrowsArgumentNullException(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var cache = CreateCache(locator.Object, timeProvider);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            cache.SetAsync("mykey", null!, new DistributedCacheEntryOptions(), TestContext.Current.CancellationToken));
    }

    [Theory, AutoMoqData]
    public async Task SetAsync_CustomDefaultSlidingExpiration_AppliesConfiguredDefault(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var proxy = new Mock<ICacheStoreService>();
        var formattedKey = $"{TestCacheStoreId}-mykey";
        locator.Setup(l => l.GetCacheStoreProxy(formattedKey, It.IsAny<CancellationToken>())).ReturnsAsync(proxy.Object);

        var cache = CreateCache(locator.Object, timeProvider, defaultSlidingExpiration: TimeSpan.FromMinutes(10));
        await cache.SetAsync("mykey", new byte[] { 1, 2, 3 }, new DistributedCacheEntryOptions(),
            TestContext.Current.CancellationToken);

        proxy.Verify(p => p.SetCachedItemAsync(
            formattedKey,
            It.IsAny<byte[]>(),
            TimeSpan.FromMinutes(10),
            null), Times.Once);
    }

    [Theory, AutoMoqData]
    public async Task SetAsync_DefaultSlidingExpirationNull_ThrowsWhenNoExpirationProvided(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var cache = CreateCache(locator.Object, timeProvider, useDefaultSlidingExpiration: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.SetAsync("mykey", new byte[] { 1, 2, 3 }, new DistributedCacheEntryOptions(),
                TestContext.Current.CancellationToken));
    }

    [Theory, AutoMoqData]
    public async Task RefreshAsync_ValidKey_CallsGetOnProxy(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var proxy = new Mock<ICacheStoreService>();
        var formattedKey = $"{TestCacheStoreId}-mykey";
        locator.Setup(l => l.GetCacheStoreProxy(formattedKey, It.IsAny<CancellationToken>())).ReturnsAsync(proxy.Object);

        var cache = CreateCache(locator.Object, timeProvider);
        await cache.RefreshAsync("mykey", TestContext.Current.CancellationToken);

        proxy.Verify(p => p.GetCachedItemAsync(formattedKey), Times.Once);
    }

    [Theory, AutoMoqData]
    public void Get_SyncMethod_ReturnsResultFromProxy(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var expected = new byte[] { 10, 20, 30 };
        var proxy = new Mock<ICacheStoreService>();
        var formattedKey = $"{TestCacheStoreId}-mykey";
        locator.Setup(l => l.GetCacheStoreProxy(formattedKey, It.IsAny<CancellationToken>())).ReturnsAsync(proxy.Object);
        proxy.Setup(p => p.GetCachedItemAsync(formattedKey)).ReturnsAsync(expected);

        var cache = CreateCache(locator.Object, timeProvider);
        var result = cache.Get("mykey");

        proxy.Verify(p => p.GetCachedItemAsync(formattedKey), Times.Once);
        Assert.Equal(expected, result);
    }

    [Theory, AutoMoqData]
    public void Remove_SyncMethod_CallsProxyRemove(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var proxy = new Mock<ICacheStoreService>();
        var formattedKey = $"{TestCacheStoreId}-mykey";
        locator.Setup(l => l.GetCacheStoreProxy(formattedKey, It.IsAny<CancellationToken>())).ReturnsAsync(proxy.Object);

        var cache = CreateCache(locator.Object, timeProvider);
        cache.Remove("mykey");

        proxy.Verify(p => p.RemoveCachedItemAsync(formattedKey), Times.Once);
    }

    [Theory, AutoMoqData]
    public async Task SetAsync_AbsoluteExpirationEqualToNow_ThrowsInvalidOperationException(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var now = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        timeProvider.SetUtcNow(now);

        var cache = CreateCache(locator.Object, timeProvider);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.SetAsync("mykey", new byte[] { 1 }, new DistributedCacheEntryOptions
            {
                AbsoluteExpiration = now
            }, TestContext.Current.CancellationToken));
    }

    [Theory, AutoMoqData]
    public void Set_SyncWrapper_CallsProxy(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var proxy = new Mock<ICacheStoreService>();
        var formattedKey = $"{TestCacheStoreId}-mykey";
        locator.Setup(l => l.GetCacheStoreProxy(formattedKey, It.IsAny<CancellationToken>())).ReturnsAsync(proxy.Object);

        var cache = CreateCache(locator.Object, timeProvider);
        cache.Set("mykey", new byte[] { 1 }, new DistributedCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(1)
        });

        locator.Verify(l => l.GetCacheStoreProxy(formattedKey, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory, AutoMoqData]
    public void Refresh_SyncWrapper_CallsProxy(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var proxy = new Mock<ICacheStoreService>();
        var formattedKey = $"{TestCacheStoreId}-mykey";
        locator.Setup(l => l.GetCacheStoreProxy(formattedKey, It.IsAny<CancellationToken>())).ReturnsAsync(proxy.Object);

        var cache = CreateCache(locator.Object, timeProvider);
        cache.Refresh("mykey");

        locator.Verify(l => l.GetCacheStoreProxy(formattedKey, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory, AutoMoqData]
    public void Dispose_WhenLocatorIsDisposable_DisposesLocator(
        [Frozen] FakeTimeProvider timeProvider)
    {
        var locatorMock = new Mock<IDistributedCacheStoreLocator>();
        locatorMock.As<IDisposable>();

        var cache = CreateCache(locatorMock.Object, timeProvider);
        cache.Dispose();

        locatorMock.As<IDisposable>().Verify(d => d.Dispose(), Times.Once);
    }
}
