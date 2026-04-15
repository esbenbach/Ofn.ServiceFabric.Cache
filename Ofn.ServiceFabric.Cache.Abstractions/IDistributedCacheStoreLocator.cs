namespace Ofn.ServiceFabric.Cache.Abstractions
{
    using System.Threading.Tasks;

    /// <summary>
    /// Resolves the SF remoting proxy for the cache partition that owns a given key.
    /// </summary>
    public interface IDistributedCacheStoreLocator
    {
        /// <summary>
        /// Returns the <see cref="ICacheStoreService"/> proxy for the partition responsible for <paramref name="cacheKey"/>.
        /// </summary>
        /// <param name="cacheKey">The cache key used to determine partition ownership.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>The <see cref="ICacheStoreService"/> proxy for the owning partition.</returns>
        Task<ICacheStoreService> GetCacheStoreProxy(string cacheKey, CancellationToken cancellationToken = default);
    }
}