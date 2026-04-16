namespace Ofn.ServiceFabric.Cache.Client;

using System.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Ofn.ServiceFabric.Cache.Abstractions;

/// <summary>
/// Implements <see cref="IDistributedCache"/> backed by a Service Fabric stateful cache store,
/// routing keys to the correct partition via <see cref="IDistributedCacheStoreLocator"/>.
/// </summary>
public class ServiceFabricDistributedCache : IDistributedCache, IDisposable
{
    private readonly IDistributedCacheStoreLocator _distributedCacheStoreLocator;

    private readonly TimeProvider _timeProvider;

    private readonly Guid _cacheStoreId;

    private readonly string _cacheStoreIdString;

    private readonly string _keyPrefix;

    private readonly TimeSpan? _defaultSlidingExpiration;

    /// <summary>Pre-populated <see cref="TagList"/> with the <c>cache_store_id</c> tag.</summary>
    private readonly TagList _storeIdTag;

    /// <summary>
    /// Initializes a new <see cref="ServiceFabricDistributedCache"/>.
    /// </summary>
    /// <param name="options">Cache options including the store ID and default expiration.</param>
    /// <param name="distributedCacheStoreLocator">Locator that resolves the cache store proxy for a given key.</param>
    /// <param name="timeProvider">Time provider used for computing absolute expiration values.</param>
    public ServiceFabricDistributedCache(IOptions<ServiceFabricCacheOptions> options, IDistributedCacheStoreLocator distributedCacheStoreLocator, TimeProvider timeProvider)
    {
        _cacheStoreId = options.Value.CacheStoreId;
        _cacheStoreIdString = _cacheStoreId.ToString();
        _keyPrefix = $"{_cacheStoreIdString}-";
        _storeIdTag = new TagList { { "cache_store_id", _cacheStoreIdString } };
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

    /// <summary>
    /// Retrieves the cached bytes for <paramref name="key"/>, or <c>null</c> if absent.
    /// </summary>
    /// <param name="key">The cache key to retrieve.</param>
    /// <param name="token">A token to cancel the operation.</param>
    /// <returns>The cached bytes, or <c>null</c> if the entry does not exist.</returns>
    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        key = FormatCacheKey(key);
        var sw = Stopwatch.StartNew();
        var status = "success";
        try
        {
            var proxy = await _distributedCacheStoreLocator.GetCacheStoreProxy(key, token).ConfigureAwait(false);
            var result = await proxy.GetCachedItemAsync(key).ConfigureAwait(false);
            CacheClientMetrics.Gets.Add(1, new TagList { { "result", result != null ? "hit" : "miss" }, { "cache_store_id", _cacheStoreIdString } });
            return result;
        }
        catch
        {
            status = "error";
            throw;
        }
        finally
        {
            sw.Stop();
            CacheClientMetrics.OperationDuration.Record(
                sw.Elapsed.TotalMilliseconds,
                new TagList { { "operation", "get" }, { "cache_store_id", _cacheStoreIdString }, { "status", status } });
        }
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

    /// <summary>
    /// Resets the sliding expiration for <paramref name="key"/> by performing a get.
    /// </summary>
    /// <param name="key">The cache key whose expiration window should be reset.</param>
    /// <param name="token">A token to cancel the operation.</param>
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

    /// <summary>
    /// Removes the cache entry for <paramref name="key"/>.
    /// </summary>
    /// <param name="key">The cache key to remove.</param>
    /// <param name="token">A token to cancel the operation.</param>
    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        key = FormatCacheKey(key);
        var sw = Stopwatch.StartNew();
        var status = "success";
        try
        {
            var proxy = await _distributedCacheStoreLocator.GetCacheStoreProxy(key, token).ConfigureAwait(false);
            await proxy.RemoveCachedItemAsync(key).ConfigureAwait(false);
        }
        catch
        {
            status = "error";
            throw;
        }
        finally
        {
            sw.Stop();
            CacheClientMetrics.OperationDuration.Record(
                sw.Elapsed.TotalMilliseconds,
                new TagList { { "operation", "remove" }, { "cache_store_id", _cacheStoreIdString }, { "status", status } });
        }
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

    /// <summary>
    /// Stores <paramref name="value"/> under <paramref name="key"/> with the supplied expiration options.
    /// Throws <see cref="InvalidOperationException"/> when no expiration is provided and
    /// <see cref="ServiceFabricCacheOptions.DefaultSlidingExpiration"/> is <c>null</c>.
    /// </summary>
    /// <param name="key">The cache key to store.</param>
    /// <param name="value">The raw bytes to cache.</param>
    /// <param name="options">Expiration options for the cache entry.</param>
    /// <param name="token">A token to cancel the operation.</param>
    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        var absoluteExpireTime = GetAbsoluteExpiration(_timeProvider.GetUtcNow(), options);
        if (absoluteExpireTime == null && options.SlidingExpiration == null)
        {
            if (_defaultSlidingExpiration.HasValue)
            {
                options.SlidingExpiration = _defaultSlidingExpiration.Value;
                CacheClientMetrics.DefaultExpirationApplied.Add(1, _storeIdTag);
            }
            else
                throw new InvalidOperationException(
                    "No expiration was provided and DefaultSlidingExpiration is not configured. " +
                    "Either set an expiration on the DistributedCacheEntryOptions or configure DefaultSlidingExpiration in ServiceFabricCacheOptions.");
        }

        ValidateOptions(options.SlidingExpiration, absoluteExpireTime);

        var sw = Stopwatch.StartNew();
        var status = "success";
        try
        {
            CacheClientMetrics.SetValueSize.Record(value.Length, _storeIdTag);
            key = FormatCacheKey(key);
            var proxy = await _distributedCacheStoreLocator.GetCacheStoreProxy(key, token).ConfigureAwait(false);
            await proxy.SetCachedItemAsync(key, value, options.SlidingExpiration, absoluteExpireTime).ConfigureAwait(false);
        }
        catch
        {
            status = "error";
            throw;
        }
        finally
        {
            sw.Stop();
            CacheClientMetrics.OperationDuration.Record(
                sw.Elapsed.TotalMilliseconds,
                new TagList { { "operation", "set" }, { "cache_store_id", _cacheStoreIdString }, { "status", status } });
        }
    }

    private static DateTimeOffset? GetAbsoluteExpiration(DateTimeOffset utcNow, DistributedCacheEntryOptions options)
    {
        if (options.AbsoluteExpirationRelativeToNow.HasValue)
            return utcNow.Add(options.AbsoluteExpirationRelativeToNow.Value);

        if (options.AbsoluteExpiration.HasValue)
        {
            if (options.AbsoluteExpiration.Value <= utcNow)
                throw new InvalidOperationException("The absolute expiration value must be in the future.");
            return options.AbsoluteExpiration.Value;
        }

        return null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_distributedCacheStoreLocator is IDisposable disposable)
            disposable.Dispose();
    }

    private static void ValidateOptions(TimeSpan? slidingExpiration, DateTimeOffset? absoluteExpiration)
    {
        if (!slidingExpiration.HasValue && !absoluteExpiration.HasValue)
            throw new InvalidOperationException("Either absolute or sliding expiration needs to be provided.");
    }

    private string FormatCacheKey(string key) => string.Concat(_keyPrefix, key);
}
