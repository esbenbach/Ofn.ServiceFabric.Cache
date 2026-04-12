namespace Ofn.ServiceFabric.Cache.Client;

using System.Collections.Concurrent;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Query;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.ServiceFabric.Services.Client;
using Microsoft.ServiceFabric.Services.Remoting.Client;
using Microsoft.ServiceFabric.Services.Remoting.V2.FabricTransport.Client;
using Ofn.ServiceFabric.Cache.Abstractions;

public class DistributedCacheStoreLocator : IDistributedCacheStoreLocator, IDisposable
{
    private const string CacheStoreProperty = "CacheStore";

    private const string CacheStorePropertyValue = "true";

    private const string ListenerName = "CacheStoreServiceListener";

    private Uri serviceUri;

    private readonly string endpointName;

    private readonly Lazy<FabricClient> _lazyFabricClient;

    private FabricClient fabricClient => _lazyFabricClient.Value;

    private ServicePartitionList? _partitionList;

    private readonly SemaphoreSlim _partitionListLock = new SemaphoreSlim(1, 1);

    private readonly ConcurrentDictionary<Guid, ICacheStoreService> cacheStores;

    private bool _disposed;

    public DistributedCacheStoreLocator(IOptions<ServiceFabricCacheOptions> options)
    {
        var fabricOptions = options.Value;
        this.serviceUri = fabricOptions.CacheStoreServiceUri;
        this.endpointName = fabricOptions.CacheStoreEndpointName ?? ListenerName;

        _lazyFabricClient = new Lazy<FabricClient>(() => new FabricClient());
        this.cacheStores = new ConcurrentDictionary<Guid, ICacheStoreService>();
    }

    public async Task<ICacheStoreService> GetCacheStoreProxy(string cacheKey)
    {
        // Try to locate a cache store if one is not configured
        if (serviceUri == null)
        {
            serviceUri = await LocateCacheStoreAsync().ConfigureAwait(false);
            if (serviceUri == null)
            {
                throw new CacheStoreNotFoundException("Cache store not found in Service Fabric cluster.  Try setting the 'CacheStoreServiceUri' configuration option to the location of your cache store.");
            }
        }

        var partitionInformation = await GetPartitionInformationForCacheKey(cacheKey).ConfigureAwait(false);

        return cacheStores.GetOrAdd(partitionInformation.Id, _ =>
        {
            var info = (Int64RangePartitionInformation)partitionInformation;
            var resolvedPartition = new ServicePartitionKey(info.LowKey);
            return CreateCacheStoreProxy(serviceUri, resolvedPartition, endpointName);
        });
    }

    protected internal virtual ICacheStoreService CreateCacheStoreProxy(Uri uri, ServicePartitionKey partitionKey, string endpoint)
    {
        var proxyFactory = new ServiceProxyFactory(_ => new FabricTransportServiceRemotingClientFactory());
        return proxyFactory.CreateServiceProxy<ICacheStoreService>(
            uri,
            partitionKey,
            Microsoft.ServiceFabric.Services.Communication.Client.TargetReplicaSelector.Default,
            endpoint);
    }

    private async Task<ServicePartitionInformation> GetPartitionInformationForCacheKey(string cacheKey)
    {
        using var md5 = MD5.Create();
        var value = md5.ComputeHash(Encoding.ASCII.GetBytes(cacheKey));
        var key = BitConverter.ToInt64(value, 0);

        if (_partitionList == null)
        {
            await _partitionListLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_partitionList == null)
                {
                    _partitionList = await FetchPartitionListAsync(serviceUri).ConfigureAwait(false);
                }
            }
            finally
            {
                _partitionListLock.Release();
            }
        }

        var partition = _partitionList.Single(p =>
            ((Int64RangePartitionInformation)p.PartitionInformation).LowKey <= key &&
            ((Int64RangePartitionInformation)p.PartitionInformation).HighKey >= key);

        return partition.PartitionInformation;
    }

    protected internal virtual Task<ServicePartitionList> FetchPartitionListAsync(Uri uri)
        => fabricClient.QueryManager.GetPartitionListAsync(uri);

    private async Task<Uri> LocateCacheStoreAsync()
    {
        try
        {
            bool hasPages = true;
            var query = new ApplicationQueryDescription() { MaxResults = 50 };

            while (hasPages)
            {
                var apps = await fabricClient.QueryManager.GetApplicationPagedListAsync(query).ConfigureAwait(false);

                query.ContinuationToken = apps.ContinuationToken;

                hasPages = !string.IsNullOrEmpty(query.ContinuationToken);

                foreach (var app in apps)
                {
                    var serviceName = await LocateCacheStoreServiceInApplicationAsync(app.ApplicationName).ConfigureAwait(false);
                    if (serviceName != null)
                        return serviceName;
                }
            }
        }
        catch { }

        return null;
    }

    private async Task<Uri> LocateCacheStoreServiceInApplicationAsync(Uri applicationName)
    {
        try
        {
            bool hasPages = true;
            var query = new ServiceQueryDescription(applicationName) { MaxResults = 50 };

            while (hasPages)
            {
                var services = await fabricClient.QueryManager.GetServicePagedListAsync(query).ConfigureAwait(false);

                query.ContinuationToken = services.ContinuationToken;

                hasPages = !string.IsNullOrEmpty(query.ContinuationToken);

                foreach (var service in services)
                {
                    var found = await IsCacheStore(service.ServiceName).ConfigureAwait(false);
                    if (found)
                        return service.ServiceName;
                }
            }
        }
        catch { }

        return null;
    }

    private async Task<bool> IsCacheStore(Uri serviceName)
    {
        try
        {
            var isCacheStore = await fabricClient.PropertyManager.GetPropertyAsync(serviceName, CacheStoreProperty).ConfigureAwait(false);
            return isCacheStore.GetValue<string>() == CacheStorePropertyValue;
        }
        catch { }

        return false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _partitionListLock.Dispose();
            if (_lazyFabricClient.IsValueCreated)
                _lazyFabricClient.Value.Dispose();
            _disposed = true;
        }
    }
}
