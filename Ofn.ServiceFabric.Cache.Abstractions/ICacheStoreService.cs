namespace Ofn.ServiceFabric.Cache.Abstractions
{
    using Microsoft.ServiceFabric.Services.Remoting;
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// SF remoting interface for a single cache store partition.
    /// </summary>
    public interface ICacheStoreService : IService
    {
        /// <summary>
        /// Returns the cached byte array for <paramref name="key"/>, or <c>null</c> if absent or expired.
        /// </summary>
        /// <param name="key">The cache key to look up.</param>
        /// <returns>The cached bytes, or <c>null</c> if the entry is absent or has expired.</returns>
        Task<byte[]?> GetCachedItemAsync(string key);

        /// <summary>
        /// Inserts or updates the entry for <paramref name="key"/>.
        /// </summary>
        /// <param name="key">The cache key to store.</param>
        /// <param name="value">The raw bytes to cache.</param>
        /// <param name="slidingExpiration">Optional sliding expiration window.</param>
        /// <param name="absoluteExpiration">Optional hard expiry timestamp.</param>
        Task SetCachedItemAsync(string key, byte[] value, TimeSpan? slidingExpiration, DateTimeOffset? absoluteExpiration);

        /// <summary>
        /// Removes the entry for <paramref name="key"/> if it exists.
        /// </summary>
        /// <param name="key">The cache key to remove.</param>
        Task RemoveCachedItemAsync(string key);
    }
}
