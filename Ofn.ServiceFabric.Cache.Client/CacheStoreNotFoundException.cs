using System;

namespace Ofn.ServiceFabric.Cache
{
    class CacheStoreNotFoundException : Exception
    {
        public CacheStoreNotFoundException(string message) : base(message)
        {

        }
    }
}
