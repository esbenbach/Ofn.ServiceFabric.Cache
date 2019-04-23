namespace Ofn.ServiceFabric.Cache
{
    using System;

    public class CacheStoreNotFoundException : Exception
    {
        public CacheStoreNotFoundException(string message) : base(message)
        {
        }
    }
}
