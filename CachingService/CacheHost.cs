namespace CachingService;

using System.Fabric;
using Microsoft.Extensions.Logging;
using Ofn.ServiceFabric.Cache;
using Ofn.ServiceFabric.Cache.Abstractions;

/// <summary>
/// 
/// </summary>
internal sealed class CacheHost : BaseCacheStoreService
{
    public CacheHost(StatefulServiceContext context, ILogger<ICacheStoreService> logger)
        : base(context, logger: logger)
    { }
}
