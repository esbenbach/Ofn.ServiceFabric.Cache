namespace Ofn.ServiceFabric.Cache.UnitTests;

using AutoFixture.Xunit3;
using Microsoft.Extensions.Caching.Distributed;
using Moq;
using Ofn.ServiceFabric.Cache.Abstractions;
using Ofn.ServiceFabric.Cache.Client;
using Xunit;

public class ServiceFabricDistributedCacheTest
{
    private static readonly Guid TestCacheStoreId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static ServiceFabricDistributedCache CreateCache(
        IDistributedCacheStoreLocator locator,
        TimeProvider timeProvider,
        Guid? cacheStoreId = null)
    {
        var options = new ServiceFabricCacheOptions { CacheStoreId = cacheStoreId ?? TestCacheStoreId };
        return new ServiceFabricDistributedCache(options, locator, timeProvider);
    }

    [Theory, AutoMoqData]
    public async Task GetAsync_ValidKey_CallsProxyWithFormattedKey(
        [Frozen] Mock<IDistributedCacheStoreLocator> locator,
        [Frozen] FakeTimeProvider timeProvider)
    {
        var proxy = new Mock<ICacheStoreService>();
        var expectedKey = $"{TestCacheStoreId}-mykey";
        locator.Setup(l => l.GetCacheStoreProxy(expectedKey)).ReturnsAsync(proxy.Object);

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
        locator.Setup(l => l.GetCacheStoreProxy(formattedKey)).ReturnsAsync(proxy.Object);

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
        locator.Setup(l => l.GetCacheStoreProxy(formattedKey)).ReturnsAsync(proxy.Object);

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
        locator.Setup(l => l.GetCacheStoreProxy(formattedKey)).ReturnsAsync(proxy.Object);

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
        locator.Setup(l => l.GetCacheStoreProxy(formattedKey)).ReturnsAsync(proxy.Object);

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
}
