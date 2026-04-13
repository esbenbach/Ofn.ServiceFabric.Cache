namespace Ofn.ServiceFabric.Cache.Client;

using System.Diagnostics.Metrics;

internal static class CacheClientMetrics
{
    internal static readonly Meter Meter = new("Ofn.ServiceFabric.Cache.Client", "1.0");

    // cache.client.operation.duration — end-to-end latency per cache operation
    internal static readonly Histogram<double> OperationDuration =
        Meter.CreateHistogram<double>("cache.client.operation.duration", "ms", "Client-side cache operation latency");

    // cache.client.gets — hit/miss outcomes for Get operations
    internal static readonly Counter<long> Gets =
        Meter.CreateCounter<long>("cache.client.gets", "{operations}", "Cache get operation results as seen by the client");

    // cache.client.value.size — payload size written on Set
    internal static readonly Histogram<long> SetValueSize =
        Meter.CreateHistogram<long>("cache.client.value.size", "By", "Size of values written to the cache");

    // cache.client.default_expiration_applied — times the DefaultSlidingExpiration fallback was used
    internal static readonly Counter<long> DefaultExpirationApplied =
        Meter.CreateCounter<long>("cache.client.default_expiration_applied", "{operations}", "Times the DefaultSlidingExpiration fallback was applied because no expiration was specified by the caller");

    // cache.client.discovery.duration — latency of auto-discovering the cache store service URI
    internal static readonly Histogram<double> DiscoveryDuration =
        Meter.CreateHistogram<double>("cache.client.discovery.duration", "ms", "Latency of auto-discovering the cache store service URI");

    // cache.client.discovery.failures — number of service discovery failures
    internal static readonly Counter<long> DiscoveryFailures =
        Meter.CreateCounter<long>("cache.client.discovery.failures", "{failures}", "Number of cache store service discovery failures");

    // cache.client.partition_list.refresh.duration — latency of fetching the SF partition list
    internal static readonly Histogram<double> PartitionListRefreshDuration =
        Meter.CreateHistogram<double>("cache.client.partition_list.refresh.duration", "ms", "Latency of fetching the partition list from the SF cluster");
}
