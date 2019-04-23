namespace CacheHost
{
    using System.Fabric;
    using Ofn.ServiceFabric.Cache;

    /// <summary>
    /// 
    /// </summary>
    /// <seealso cref="Ofn.ServiceFabric.Cache.BaseCacheStoreService" />
    internal sealed class CacheHost : BaseCacheStoreService
    {
        public CacheHost(StatefulServiceContext context)
            : base(context)
        { }
    }
}
