using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Ofn.ServiceFabric.Cache.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class StringAllocationBenchmarks
{
    private const string CacheKey = "my-cache-key-12345";
    private const int MaxBackOffFactor = 1024;

    private Guid _cacheStoreId;

    // HIGH-2: cached prefix
    private string _keyPrefix = null!;

    // HIGH-3: cached string + pre-built TagList instances
    private string _cacheStoreIdString = null!;
    private TagList _storeIdTag;
    private TagList _getsHitTag;
    private TagList _opGetSuccessTag;

    // LOW-1: cached partition id string
    private Guid _partitionGuid;
    private string _cachedPartitionId = null!;

    // MED-4: attempts parameter
    [Params(0, 3, 7, 9)]
    public int Attempts { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _cacheStoreId = Guid.NewGuid();
        _keyPrefix = $"{_cacheStoreId}-";
        _cacheStoreIdString = _cacheStoreId.ToString();
        _storeIdTag = new TagList { { "cache_store_id", _cacheStoreIdString } };
        _getsHitTag = new TagList { { "result", "hit" }, { "cache_store_id", _cacheStoreIdString } };
        _opGetSuccessTag = new TagList { { "operation", "get" }, { "cache_store_id", _cacheStoreIdString }, { "status", "success" } };
        _partitionGuid = Guid.NewGuid();
        _cachedPartitionId = _partitionGuid.ToString();
    }

    // ── HIGH-1: MD5 hashing in GetPartitionInformationForCacheKey ─────────

    /// <summary>Current production code: allocates MD5 instance, byte[] for UTF-8 input, and byte[] for hash output.</summary>
    [Benchmark]
    public long High1_Old()
    {
        using var md5 = MD5.Create();
        var value = md5.ComputeHash(Encoding.UTF8.GetBytes(CacheKey));
        return BitConverter.ToInt64(value, 0);
    }

    /// <summary>Proposed: stackalloc for both buffers; zero heap allocations.</summary>
    [Benchmark]
    public long High1_New()
    {
        Span<byte> inputBuffer = stackalloc byte[Encoding.UTF8.GetMaxByteCount(CacheKey.Length)];
        Encoding.UTF8.TryGetBytes(CacheKey, inputBuffer, out int bytesWritten);
        Span<byte> hashBuffer = stackalloc byte[16];
        MD5.HashData(inputBuffer[..bytesWritten], hashBuffer);
        return MemoryMarshal.Read<long>(hashBuffer);
    }

    // ── HIGH-2: FormatCacheKey string interpolation ────────────────────────

    /// <summary>Current production code: Guid.ToString() + string interpolation on every call.</summary>
    [Benchmark]
    public string High2_Old() => $"{_cacheStoreId}-{CacheKey}";

    /// <summary>Proposed: prefix cached once in ctor; only one string concat per call.</summary>
    [Benchmark]
    public string High2_New() => string.Concat(_keyPrefix, CacheKey);

    // ── HIGH-3: StoreIdTag — single-tag property ──────────────────────────

    /// <summary>Current production code: Guid.ToString() + TagList allocation on every property access.</summary>
    [Benchmark]
    public TagList High3_Old_Single() => new TagList { { "cache_store_id", _cacheStoreId.ToString() } };

    /// <summary>Proposed: return pre-built readonly TagList field; zero allocations.</summary>
    [Benchmark]
    public TagList High3_New_Single() => _storeIdTag;

    // ── HIGH-3: simulate full GetAsync — two TagList creations per call ────

    /// <summary>
    /// Current production code for a GetAsync call: two separate TagList allocations,
    /// each calling _cacheStoreId.ToString().
    /// </summary>
    [Benchmark]
    public TagList High3_Old_GetAsync()
    {
        // mirrors: Gets.Add(1, new TagList { { "result", hitOrMiss }, { "cache_store_id", _cacheStoreId.ToString() } })
        _ = new TagList { { "result", "hit" }, { "cache_store_id", _cacheStoreId.ToString() } };
        // mirrors: OperationDuration.Record(ms, new TagList { { "operation", "get" }, { "cache_store_id", _cacheStoreId.ToString() }, { "status", status } })
        return new TagList { { "operation", "get" }, { "cache_store_id", _cacheStoreId.ToString() }, { "status", "success" } };
    }

    /// <summary>Proposed: both TagLists are cached fields; zero Guid.ToString() calls per operation.</summary>
    [Benchmark]
    public TagList High3_New_GetAsync()
    {
        _ = _getsHitTag;
        return _opGetSuccessTag;
    }

    // ── MED-4: Math.Pow vs bit-shift in RetryHelper ───────────────────────

    /// <summary>Current production code: floating-point Math.Pow for integer exponentiation.</summary>
    [Benchmark]
    public int Med4_Old() => (int)Math.Min(Math.Pow(2, Attempts), MaxBackOffFactor) + 1;

    /// <summary>Proposed: integer bit-shift; no floating-point conversion.</summary>
    [Benchmark]
    public int Med4_New() => Math.Min(1 << Attempts, MaxBackOffFactor) + 1;

    // ── LOW-1: Partition?.Id.ToString() vs cached string ─────────────────

    /// <summary>Current production code: Guid.ToString() on every logging call.</summary>
    [Benchmark]
    public string Low1_Old() => _partitionGuid.ToString() ?? string.Empty;

    /// <summary>Proposed: string cached once in OnOpenAsync; field read on every logging call.</summary>
    [Benchmark]
    public string Low1_New() => _cachedPartitionId;
}
