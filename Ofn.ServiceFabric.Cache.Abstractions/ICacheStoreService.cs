namespace Ofn.ServiceFabric.Cache.Abstractions
{
    using Microsoft.ServiceFabric.Services.Remoting;
    using System;
    using System.Threading.Tasks;

    public interface ICacheStoreService : IService
    {
        Task<byte[]> GetCachedItemAsync(string key);

        Task SetCachedItemAsync(string key, byte[] value, TimeSpan? slidingExpiration, DateTimeOffset? absoluteExpiration);

        Task RemoveCachedItemAsync(string key);
    }
}
