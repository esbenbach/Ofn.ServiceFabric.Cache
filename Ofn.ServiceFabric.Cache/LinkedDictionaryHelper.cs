using Microsoft.ServiceFabric.Data;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
[assembly: InternalsVisibleTo("Ofn.ServiceFabric.Cache.UnitTests")]

namespace Ofn.ServiceFabric.Cache;

public class LinkedDictionaryHelper
{
    private readonly Func<string, Task<ConditionalValue<CachedItem>>> _getCacheItem;
    private readonly int _byteSizeOffset;

    public LinkedDictionaryHelper(Func<string, Task<ConditionalValue<CachedItem>>> getCacheItem) : this(getCacheItem, 0)
    {
    }

    public LinkedDictionaryHelper(Func<string, Task<ConditionalValue<CachedItem>>> getCacheItem, int byteSizeOffset)
    {
        _getCacheItem = getCacheItem;
        _byteSizeOffset = byteSizeOffset;
    }
   
    public async Task<LinkedDictionaryItemsChanged> Remove(CacheStoreMetadata cacheStoreMetadata, CachedItem cachedItem)
    {
        var before = cachedItem.BeforeCacheKey;
        var after = cachedItem.AfterCacheKey;
        var size = (cacheStoreMetadata.Size - cachedItem.Value.Length) - _byteSizeOffset;

        // only item in linked dictionary
        if (before == null && after == null)
        {
            return new LinkedDictionaryItemsChanged(new Dictionary<string, CachedItem>(), new CacheStoreMetadata(size, null, null));
        }

        // first item in linked dictionary
        if (before == null)
        {
            var afterResult = await _getCacheItem(after);
            if (!afterResult.HasValue)
                throw new InvalidOperationException($"Cache item '{after}' was expected but not found in the cache store.");
            var afterCachedItem = afterResult.Value;
            var newCachedItem = new Dictionary<string, CachedItem> { { after, new CachedItem(afterCachedItem.Value, null, afterCachedItem.AfterCacheKey, afterCachedItem.SlidingExpiration, afterCachedItem.AbsoluteExpiration) } };
            return new LinkedDictionaryItemsChanged(newCachedItem, new CacheStoreMetadata(size, after, cacheStoreMetadata.LastCacheKey));
        }

        // last item in linked dictionary
        if (after == null)
        {
            var beforeResult = await _getCacheItem(before);
            if (!beforeResult.HasValue)
                throw new InvalidOperationException($"Cache item '{before}' was expected but not found in the cache store.");
            var beforeCachedItem = beforeResult.Value;
            var newCachedItem = new Dictionary<string, CachedItem> { { before, new CachedItem(beforeCachedItem.Value, beforeCachedItem.BeforeCacheKey, null, beforeCachedItem.SlidingExpiration, beforeCachedItem.AbsoluteExpiration) } };
            return new LinkedDictionaryItemsChanged(newCachedItem, new CacheStoreMetadata(size, cacheStoreMetadata.FirstCacheKey, before));
        }

        // middle item in linked dictionary

        var beforeItemResult = await _getCacheItem(before);
        if (!beforeItemResult.HasValue)
            throw new InvalidOperationException($"Cache item '{before}' was expected but not found in the cache store.");
        var beforeItem = beforeItemResult.Value;

        var afterItemResult = await _getCacheItem(after);
        if (!afterItemResult.HasValue)
            throw new InvalidOperationException($"Cache item '{after}' was expected but not found in the cache store.");
        var afterItem = afterItemResult.Value;

        var metadata = new CacheStoreMetadata(size, cacheStoreMetadata.FirstCacheKey, cacheStoreMetadata.LastCacheKey);

        var newCachedItems = new Dictionary<string, CachedItem>();
        // add new before cached item
        newCachedItems.Add(before, new CachedItem(beforeItem.Value, beforeItem.BeforeCacheKey, after, beforeItem.SlidingExpiration, beforeItem.AbsoluteExpiration));
        // add new after cached item
        newCachedItems.Add(after, new CachedItem(afterItem.Value, before, afterItem.AfterCacheKey, afterItem.SlidingExpiration, afterItem.AbsoluteExpiration));

        return new LinkedDictionaryItemsChanged(newCachedItems, metadata);
    }

    public async Task<LinkedDictionaryItemsChanged> AddLast(CacheStoreMetadata cacheStoreMetadata, string cacheItemKey, CachedItem cachedItem, byte[] newValue)
    {
        var cachedDictionary = new Dictionary<string, CachedItem>();
        var firstCacheKey = cacheItemKey;

        // set current last item to be the second from last
        if (cacheStoreMetadata.LastCacheKey != null)
        {
            var currentLastResult = await _getCacheItem(cacheStoreMetadata.LastCacheKey);
            if (!currentLastResult.HasValue)
                throw new InvalidOperationException($"Cache item '{cacheStoreMetadata.LastCacheKey}' was expected but not found in the cache store.");
            var currentLastCacheItem = currentLastResult.Value;
            firstCacheKey = cacheStoreMetadata.FirstCacheKey;
            cachedDictionary.Add(cacheStoreMetadata.LastCacheKey, new CachedItem(currentLastCacheItem.Value, currentLastCacheItem.BeforeCacheKey, cacheItemKey, currentLastCacheItem.SlidingExpiration, currentLastCacheItem.AbsoluteExpiration));
        }

        // set new cached item to be last item in list
        cachedDictionary.Add(cacheItemKey, new CachedItem(newValue, cacheStoreMetadata.LastCacheKey, null, cachedItem.SlidingExpiration, cachedItem.AbsoluteExpiration));

        // calculate size of new collection 
        var size = (cacheStoreMetadata.Size + newValue.Length) + _byteSizeOffset;

        // set new last item in the metadata
        var newCacheStoreMetadata = new CacheStoreMetadata(size, firstCacheKey, cacheItemKey);

        return new LinkedDictionaryItemsChanged(cachedDictionary, newCacheStoreMetadata);
    }
}

public class LinkedDictionaryItemsChanged
{
    public LinkedDictionaryItemsChanged(Dictionary<string, CachedItem> cachedItemsToUpdate, CacheStoreMetadata cacheStoreMetadata)
    {
        CachedItemsToUpdate = cachedItemsToUpdate;
        CacheStoreMetadata = cacheStoreMetadata;
    }

    public IReadOnlyDictionary<string, CachedItem> CachedItemsToUpdate { get; private set; }
    public CacheStoreMetadata CacheStoreMetadata { get; private set; }
}
