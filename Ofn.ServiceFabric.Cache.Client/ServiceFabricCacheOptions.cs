namespace Ofn.ServiceFabric.Cache
{
    using System;
    using Microsoft.Extensions.Options;

    public class ServiceFabricCacheOptions : IOptions<ServiceFabricCacheOptions>
    {
        public ServiceFabricCacheOptions Value => this;

        public Uri? CacheStoreServiceUri { get; set; }

        public string? CacheStoreEndpointName { get; set; }

        public Guid CacheStoreId { get; set; }
    }
}