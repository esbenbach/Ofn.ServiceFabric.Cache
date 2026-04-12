namespace Ofn.ServiceFabric.Cache.Client;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Query;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
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

    private readonly string endpointName;

    private readonly Lazy<FabricClient> _lazyFabricClient;

    private FabricClient fabricClient => _lazyFabricClient.Value;

    private volatile ServicePartitionList? _partitionList;

    private readonly SemaphoreSlim _partitionListLock = new SemaphoreSlim(1, 1);

    private readonly ServiceProxyFactory _serviceProxyFactory;

    private readonly ConcurrentDictionary<Guid, ICacheStoreService> cacheStores;

    private readonly ILogger<DistributedCacheStoreLocator> _logger;

    private readonly Lazy<Task<Uri?>> _lazyServiceUri;

    private bool _disposed;

    public DistributedCacheStoreLocator(IOptions<ServiceFabricCacheOptions> options, ILogger<DistributedCacheStoreLocator> logger)
    {
        var fabricOptions = options.Value;
        _logger = logger;
        this.endpointName = fabricOptions.CacheStoreEndpointName ?? ListenerName;

        _lazyFabricClient = new Lazy<FabricClient>(() => new FabricClient());
        _serviceProxyFactory = new ServiceProxyFactory(_ => new FabricTransportServiceRemotingClientFactory());
        this.cacheStores = new ConcurrentDictionary<Guid, ICacheStoreService>();

        var configuredUri = fabricOptions.CacheStoreServiceUri;
        _lazyServiceUri = configuredUri != null
            ? new Lazy<Task<Uri?>>(() => Task.FromResult<Uri?>(configuredUri))
            : new Lazy<Task<Uri?>>(() => DiscoverWithMetricsAsync(), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<ICacheStoreService> GetCacheStoreProxy(string cacheKey, CancellationToken cancellationToken = default)
    {
        var resolvedUri = await _lazyServiceUri.Value.ConfigureAwait(false);
        if (resolvedUri == null)
        {
            CacheClientMetrics.DiscoveryFailures.Add(1);
            throw new CacheStoreNotFoundException("Cache store not found in Service Fabric cluster.  Try setting the 'CacheStoreServiceUri' configuration option to the location of your cache store.");
        }

        var partitionInformation = await GetPartitionInformationForCacheKey(cacheKey, resolvedUri, cancellationToken).ConfigureAwait(false);

        return cacheStores.GetOrAdd(partitionInformation.Id, _ =>
        {
            if (partitionInformation is not Int64RangePartitionInformation info)
                throw new InvalidOperationException(
                    $"The cache store service at '{resolvedUri}' uses an unsupported partition scheme " +
                    $"({partitionInformation.GetType().Name}). Only Int64Range partitioning is supported.");
            var resolvedPartition = new ServicePartitionKey(info.LowKey);
            return CreateCacheStoreProxy(resolvedUri, resolvedPartition, endpointName);
        });
    }

    private async Task<Uri?> DiscoverWithMetricsAsync()
    {
        var sw = Stopwatch.StartNew();
        var uri = await LocateCacheStoreAsync(CancellationToken.None).ConfigureAwait(false);
        sw.Stop();
        CacheClientMetrics.DiscoveryDuration.Record(
            sw.Elapsed.TotalMilliseconds,
            new TagList { { "status", uri != null ? "success" : "failed" } });
        return uri;
    }

    protected internal virtual ICacheStoreService CreateCacheStoreProxy(Uri uri, ServicePartitionKey partitionKey, string endpoint)
    {
        return _serviceProxyFactory.CreateServiceProxy<ICacheStoreService>(
            uri,
            partitionKey,
            Microsoft.ServiceFabric.Services.Communication.Client.TargetReplicaSelector.Default,
            endpoint);
    }

    private async Task<ServicePartitionInformation> GetPartitionInformationForCacheKey(string cacheKey, Uri serviceUri, CancellationToken cancellationToken)
    {
        using var md5 = MD5.Create();
        var value = md5.ComputeHash(Encoding.UTF8.GetBytes(cacheKey));
        var key = BitConverter.ToInt64(value, 0);

        if (_partitionList == null)
        {
            await _partitionListLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_partitionList == null)
                {
                    _logger.LogDebug("Fetching partition list for cache store at {ServiceUri}.", serviceUri);
                    var sw = Stopwatch.StartNew();
                    _partitionList = await FetchPartitionListAsync(serviceUri, cancellationToken).ConfigureAwait(false);
                    sw.Stop();
                    CacheClientMetrics.PartitionListRefreshDuration.Record(sw.Elapsed.TotalMilliseconds);
                }
            }
            finally
            {
                _partitionListLock.Release();
            }
        }

        Partition? partition = null;
        foreach (var p in _partitionList)
        {
            if (p.PartitionInformation is Int64RangePartitionInformation range &&
                range.LowKey <= key && range.HighKey >= key)
            {
                partition = p;
                break;
            }
        }
        if (partition is null)
            throw new InvalidOperationException($"No Int64Range partition found for key hash {key}.");
        return partition.PartitionInformation;
    }

    protected internal virtual Task<ServicePartitionList> FetchPartitionListAsync(Uri uri, CancellationToken cancellationToken = default)
        => fabricClient.QueryManager.GetPartitionListAsync(uri, null, TimeSpan.FromSeconds(30), cancellationToken);

    protected internal virtual async Task<Uri?> LocateCacheStoreAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting cache store auto-discovery.");
        try
        {
            bool hasPages = true;
            var query = new ApplicationQueryDescription() { MaxResults = 50 };

            while (hasPages)
            {
                var apps = await fabricClient.QueryManager.GetApplicationPagedListAsync(query, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

                query.ContinuationToken = apps.ContinuationToken;

                hasPages = !string.IsNullOrEmpty(query.ContinuationToken);

                foreach (var app in apps)
                {
                    var serviceName = await LocateCacheStoreServiceInApplicationAsync(app.ApplicationName, cancellationToken).ConfigureAwait(false);
                    if (serviceName != null)
                    {
                        _logger.LogInformation("Cache store located at {ServiceUri}.", serviceName);
                        return serviceName;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during cache store auto-discovery; returning null.");
        }

        return null;
    }

    private async Task<Uri?> LocateCacheStoreServiceInApplicationAsync(Uri applicationName, CancellationToken cancellationToken = default)
    {
        try
        {
            bool hasPages = true;
            var query = new ServiceQueryDescription(applicationName) { MaxResults = 50 };

            while (hasPages)
            {
                var services = await fabricClient.QueryManager.GetServicePagedListAsync(query, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

                query.ContinuationToken = services.ContinuationToken;

                hasPages = !string.IsNullOrEmpty(query.ContinuationToken);

                foreach (var service in services)
                {
                    var found = await IsCacheStore(service.ServiceName, cancellationToken).ConfigureAwait(false);
                    if (found)
                        return service.ServiceName;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error querying services in application {Application}; skipping.", applicationName);
        }

        return null;
    }

    private async Task<bool> IsCacheStore(Uri serviceName, CancellationToken cancellationToken = default)
    {
        try
        {
            var isCacheStore = await fabricClient.PropertyManager.GetPropertyAsync(serviceName, CacheStoreProperty, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return isCacheStore.GetValue<string>() == CacheStorePropertyValue;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read CacheStore property from {ServiceName}; treating as non-cache service.", serviceName);
        }
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
