namespace Ofn.ServiceFabric.Cache;

using System.Diagnostics.Tracing;

[EventSource(Name = "Ofn-ServiceFabric-Cache")]
internal sealed class CacheEventSource : EventSource
{
    public static readonly CacheEventSource Log = new();

    public static class Keywords
    {
        public const EventKeywords CacheOperation = (EventKeywords)1;
        public const EventKeywords Pruning = (EventKeywords)2;
    }

    [Event(1, Level = EventLevel.Verbose, Keywords = Keywords.CacheOperation,
           Message = "GetCacheItem: key={0} partition={1}")]
    public void GetCacheItem(string key, string partitionId)
    {
        if (IsEnabled()) WriteEvent(1, key, partitionId);
    }

    [Event(2, Level = EventLevel.Verbose, Keywords = Keywords.CacheOperation,
           Message = "SetCacheItem: key={0} partition={1}")]
    public void SetCacheItem(string key, string partitionId)
    {
        if (IsEnabled()) WriteEvent(2, key, partitionId);
    }

    [Event(3, Level = EventLevel.Verbose, Keywords = Keywords.CacheOperation,
           Message = "RemoveCacheItem: key={0} partition={1}")]
    public void RemoveCacheItem(string key, string partitionId)
    {
        if (IsEnabled()) WriteEvent(3, key, partitionId);
    }

    [Event(4, Level = EventLevel.Verbose, Keywords = Keywords.Pruning,
           Message = "PruningCycleSize: currentBytes={0} maxBytes={1}")]
    public void PruningCycleSize(long currentBytes, long maxBytes)
    {
        if (IsEnabled()) WriteEvent(4, currentBytes, maxBytes);
    }

    [Event(5, Level = EventLevel.Verbose, Keywords = Keywords.Pruning,
           Message = "PruningEvicted: key={0}")]
    public void PruningEvicted(string key)
    {
        if (IsEnabled()) WriteEvent(5, key);
    }

    [Event(6, Level = EventLevel.Verbose, Keywords = Keywords.Pruning,
           Message = "PruningMoved: key={0}")]
    public void PruningMoved(string key)
    {
        if (IsEnabled()) WriteEvent(6, key);
    }
}
