namespace Ofn.ServiceFabric.Cache;

using System.Diagnostics.Metrics;

internal static class CacheMetrics
{
    internal static readonly Meter Meter = new("Ofn.ServiceFabric.Cache", "1.0");

    // Suggestion 1: cache.gets Counter
    internal static readonly Counter<long> Gets =
        Meter.CreateCounter<long>("cache.gets", "{operations}", "Number of cache get operations by result");

    // Suggestion 2: cache.operation.duration Histogram
    internal static readonly Histogram<double> OperationDuration =
        Meter.CreateHistogram<double>("cache.operation.duration", "ms", "Latency of cache operations");

    // Suggestion 3: cache.transaction.retries and cache.transaction.failures Counters
    internal static readonly Counter<long> TransactionRetries =
        Meter.CreateCounter<long>("cache.transaction.retries", "{retries}", "Number of transaction retry attempts");
    internal static readonly Counter<long> TransactionFailures =
        Meter.CreateCounter<long>("cache.transaction.failures", "{failures}", "Number of transactions that failed all retry attempts");

    // Suggestion 5: cache.evictions Counter
    internal static readonly Counter<long> Evictions =
        Meter.CreateCounter<long>("cache.evictions", "{items}", "Number of cache items evicted");

    // Suggestion 6: cache.pruning.cycles Counter
    internal static readonly Counter<long> PruningCycles =
        Meter.CreateCounter<long>("cache.pruning.cycles", "{cycles}", "Number of cache pruning cycles executed");

    // Suggestion 7: cache.item.size Histogram
    internal static readonly Histogram<long> ItemSize =
        Meter.CreateHistogram<long>("cache.item.size", "By", "Size of cache item values written");
}
