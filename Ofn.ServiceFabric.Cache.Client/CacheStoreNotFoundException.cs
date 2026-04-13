namespace Ofn.ServiceFabric.Cache.Client;

using System;

public class CacheStoreNotFoundException : Exception
{
    public CacheStoreNotFoundException(string message) : base(message)
    {
    }
}
