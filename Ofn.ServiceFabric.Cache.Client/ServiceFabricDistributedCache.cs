namespace Ofn.ServiceFabric.Cache.Client;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Ofn.ServiceFabric.Cache.Abstractions;

public class ServiceFabricDistributedCache : IDistributedCache
{
    private readonly IDistributedCacheStoreLocator _distributedCacheStoreLocator;

    private readonly TimeProvider _timeProvider;

    private readonly Guid _cacheStoreId;

    private readonly TimeSpan? _defaultSlidingExpiration;

    public ServiceFabricDistributedCache(IOptions<ServiceFabricCacheOptions> options, IDistributedCacheStoreLocator distributedCacheStoreLocator, TimeProvider timeProvider)
    {
        _cacheStoreId = options.Value.CacheStoreId;
        _defaultSlidingExpiration = options.Value.DefaultSlidingExpiration;
        _distributedCacheStoreLocator = distributedCacheStoreLocator;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Synchronous wrapper. Prefer <see cref="GetAsync"/> in async call chains.
    /// Uses <c>Task.Run</c> internally to avoid deadlocks under ASP.NET Core
    /// synchronization contexts.
    /// </summary>
    public byte[]? Get(string key)
    {
        return Task.Run(() => GetAsync(key)).GetAwaiter().GetResult();
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        key = FormatCacheKey(key);
        var proxy = await _distributedCacheStoreLocator.GetCacheStoreProxy(key).ConfigureAwait(false);
        return await proxy.GetCachedItemAsync(key).ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronous wrapper. Prefer <see cref="RefreshAsync"/> in async call chains.
    /// Uses <c>Task.Run</c> internally to avoid deadlocks under ASP.NET Core
    /// synchronization contexts.
    /// </summary>
    public void Refresh(string key)
    {
        Task.Run(() => RefreshAsync(key)).GetAwaiter().GetResult();
    }

    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await GetAsync(key, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronous wrapper. Prefer <see cref="RemoveAsync"/> in async call chains.
    /// Uses <c>Task.Run</c> internally to avoid deadlocks under ASP.NET Core
    /// synchronization contexts.
    /// </summary>
    public void Remove(string key)
    {
        Task.Run(() => RemoveAsync(key)).GetAwaiter().GetResult();
    }

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        key = FormatCacheKey(key);
        var proxy = await _distributedCacheStoreLocator.GetCacheStoreProxy(key).ConfigureAwait(false);
        await proxy.RemoveCachedItemAsync(key).ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronous wrapper. Prefer <see cref="SetAsync"/> in async call chains.
    /// Uses <c>Task.Run</c> internally to avoid deadlocks under ASP.NET Core
    /// synchronization contexts.
    /// </summary>
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        Task.Run(() => SetAsync(key, value, options)).GetAwaiter().GetResult();
    }

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        var absoluteExpireTime = GetAbsoluteExpiration(_timeProvider.GetUtcNow(), options);
        if (absoluteExpireTime == null && options.SlidingExpiration == null)
        {
            if (_defaultSlidingExpiration.HasValue)
                options.SlidingExpiration = _defaultSlidingExpiration.Value;
            else
                throw new InvalidOperationException(
                    "No expiration was provided and DefaultSlidingExpiration is not configured. " +
                    "Either set an expiration on the DistributedCacheEntryOptions or configure DefaultSlidingExpiration in ServiceFabricCacheOptions.");
        }

        ValidateOptions(options.SlidingExpiration, absoluteExpireTime);

        key = FormatCacheKey(key);
        var proxy = await _distributedCacheStoreLocator.GetCacheStoreProxy(key).ConfigureAwait(false);
        await proxy.SetCachedItemAsync(key, value, options.SlidingExpiration, absoluteExpireTime).ConfigureAwait(false);
    }

    private static DateTimeOffset? GetAbsoluteExpiration(DateTimeOffset utcNow, DistributedCacheEntryOptions options)
    {
        var expireTime = new DateTimeOffset?();
        if (options.AbsoluteExpirationRelativeToNow.HasValue)
            expireTime = new DateTimeOffset?(utcNow.Add(options.AbsoluteExpirationRelativeToNow.Value));
        else if (options.AbsoluteExpiration.HasValue)
        {
            if (options.AbsoluteExpiration.Value <= utcNow)
                throw new InvalidOperationException("The absolute expiration value must be in the future.");
            expireTime = new DateTimeOffset?(options.AbsoluteExpiration.Value);
        }
        return expireTime;
    }

    private static void ValidateOptions(TimeSpan? slidingExpiration, DateTimeOffset? absoluteExpiration)
    {
        if (!slidingExpiration.HasValue && !absoluteExpiration.HasValue)
            throw new InvalidOperationException("Either absolute or sliding expiration needs to be provided.");
    }

    private string FormatCacheKey(string key)
    {
        return $"{_cacheStoreId}-{key}";
    }
}
