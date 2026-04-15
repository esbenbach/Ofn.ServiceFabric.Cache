namespace Ofn.ServiceFabric.Cache.Client;

using System;

/// <summary>
/// Thrown when the cache store service cannot be located in the Service Fabric cluster.
/// </summary>
public class CacheStoreNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new <see cref="CacheStoreNotFoundException"/> with <paramref name="message"/>.
    /// </summary>
    /// <param name="message">A message describing the failure to locate the cache store.</param>
    public CacheStoreNotFoundException(string message) : base(message)
    {
    }
}
