namespace CacheHost
{
    using System.Fabric;
    using Microsoft.Extensions.Logging;
    using Ofn.ServiceFabric.Cache;
    using Ofn.ServiceFabric.Cache.Abstractions;

    /// <summary>
    /// 
    /// </summary>
    /// <seealso cref="Ofn.ServiceFabric.Cache.BaseCacheStoreService" />
    internal sealed class CacheHost : BaseCacheStoreService
    {
        public CacheHost(StatefulServiceContext context, ILogger<ICacheStoreService> logger)
            : base(context, logger: logger)
        { }
    }
}
