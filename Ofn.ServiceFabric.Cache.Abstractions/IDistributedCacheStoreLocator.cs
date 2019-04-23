namespace Ofn.ServiceFabric.Cache.Abstractions
{
    using System.Threading.Tasks;

    public interface IDistributedCacheStoreLocator
    {
        Task<ICacheStoreService> GetCacheStoreProxy(string cacheKey);
    }
}