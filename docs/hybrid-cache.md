# Using HybridCache with Ofn.ServiceFabric.Cache

[`HybridCache`](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid) (introduced in .NET 9) is a two-level cache:

- **L1 – in-process memory** (`MemoryCache`) for ultra-fast, zero-allocation reads of recently used values.
- **L2 – distributed backing store** (`IDistributedCache`) for sharing values across service instances and surviving restarts.

Because `Ofn.ServiceFabric.Cache.Client` registers a full `IDistributedCache` implementation, wiring it up as HybridCache's L2 requires no additional glue code.

---

## Prerequisites

| Requirement | Details |
|---|---|
| .NET | 10.0 or later |
| SF Cache client | `Ofn.ServiceFabric.Cache.Client` NuGet package |
| HybridCache | `Microsoft.Extensions.Caching.Hybrid` NuGet package (in-box from .NET 9) |

---

## Registration

### Minimal setup (auto-discovers the cache store)

```csharp
// Program.cs / Startup.cs
builder.Services.AddDistributedServiceFabricCache();   // registers IDistributedCache (L2)
builder.Services.AddHybridCache();                      // registers HybridCache on top of it
```

When no `CacheStoreServiceUri` is provided the client walks the local SF cluster and finds the first service that advertises itself as a cache store. This is fine for single-store scenarios.

### Explicit service URI

```csharp
builder.Services.AddDistributedServiceFabricCache(options =>
{
    options.CacheStoreServiceUri = new Uri("fabric:/MyApp/MyCacheService");
    options.CacheStoreEndpointName = "CacheServiceEndpoint";  // optional, matches listener name
    options.CacheStoreId = Guid.Parse("00000000-0000-0000-0000-000000000001"); // logical cache namespace
});

builder.Services.AddHybridCache(options =>
{
    // Maximum total size of the L1 in-process cache
    options.MaximumPayloadBytes = 1024 * 1024;   // 1 MB per entry (default 1 MB)

    // Default L1 expiry (entries are re-fetched from L2 / the source after this)
    options.DefaultEntryOptions = new HybridCacheEntryOptions
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(1),
    };
});
```

> **`CacheStoreId`** acts as a key namespace — all keys written through this client are prefixed with the GUID. Use a different GUID per logical application or environment to share a single cache store service without key collisions.

---

## Basic usage

Inject `HybridCache` wherever you need caching. The primary API is `GetOrCreateAsync`, which implements the cache-aside pattern atomically:

```csharp
public class ProductService(HybridCache cache)
{
    public async Task<Product> GetProductAsync(int id, CancellationToken ct = default)
    {
        return await cache.GetOrCreateAsync(
            key: $"product:{id}",
            factory: async token => await FetchFromDatabaseAsync(id, token),
            cancellationToken: ct
        );
    }
}
```

`HybridCache` checks L1, then L2 (`IDistributedCache` → Service Fabric), then calls `factory` if neither has the value. The result is stored in both layers.

---

## Expiration

HybridCache and `IDistributedCache` each have their own expiration controls.

| Layer | Controlled by |
|---|---|
| L1 (in-process) | `HybridCacheEntryOptions.LocalCacheExpiration` |
| L2 (Service Fabric) | `HybridCacheEntryOptions.Expiration` |

```csharp
var options = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromHours(1),        // how long to keep the value in SF (L2)
    LocalCacheExpiration = TimeSpan.FromMinutes(5), // how long to keep it in memory (L1)
};

await cache.GetOrCreateAsync("my-key", factory, options, cancellationToken: ct);
```

> **Note:** If you do not provide expiration options here, HybridCache uses `DefaultEntryOptions` configured at registration time. If neither is set, `ServiceFabricDistributedCache` applies a **60-second sliding expiration** automatically. Set `ServiceFabricCacheOptions.DefaultSlidingExpiration = null` to require explicit expiration on every call.

---

## Setting a value explicitly

```csharp
await cache.SetAsync("my-key", myValue, cancellationToken: ct);
```

To set with custom per-call expiration:

```csharp
await cache.SetAsync(
    "my-key",
    myValue,
    new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(10) },
    cancellationToken: ct);
```

---

## Removing / invalidating a value

```csharp
await cache.RemoveAsync("my-key", ct);
```

This removes the entry from both L1 and L2. Note that other application instances will only have their L1 invalidated the next time they try to read the key (L1 expiry or next `GetOrCreateAsync` call).

---

## Observability / metrics

Both the Service Fabric cache client and HybridCache emit `System.Diagnostics.Metrics` counters.

### Service Fabric client meters

| Meter | Description |
|---|---|
| `Ofn.ServiceFabric.Cache.Client` | All client-side instruments |

Key instruments:

| Instrument | Type | Tags |
|---|---|---|
| `cache.client.operation.duration` | Histogram (ms) | `operation` (get/set/remove), `status`, `cache_store_id` |
| `cache.client.gets` | Counter | `result` (hit/miss), `cache_store_id` |
| `cache.client.set.value.size` | Histogram (bytes) | `cache_store_id` |
| `cache.client.default.expiration.applied` | Counter | `cache_store_id` |

### HybridCache built-in meters

HybridCache itself publishes metrics under `Microsoft.Extensions.Caching.Hybrid` (available from .NET 9).

To capture both in OpenTelemetry:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("Ofn.ServiceFabric.Cache.Client")
        .AddMeter("Ofn.ServiceFabric.Cache")
        .AddMeter("Microsoft.Extensions.Caching.Hybrid"));
```

---

## Full example

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDistributedServiceFabricCache(options =>
{
    options.CacheStoreServiceUri = new Uri("fabric:/MyApp/MyCacheService");
    options.CacheStoreId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    options.DefaultSlidingExpiration = null; // require explicit expiration
});

builder.Services.AddHybridCache(options =>
{
    options.DefaultEntryOptions = new HybridCacheEntryOptions
    {
        Expiration = TimeSpan.FromHours(1),
        LocalCacheExpiration = TimeSpan.FromMinutes(5),
    };
});

var app = builder.Build();

app.MapGet("/products/{id}", async (int id, HybridCache cache, CancellationToken ct) =>
{
    var product = await cache.GetOrCreateAsync(
        $"product:{id}",
        async token => await LoadProductFromDbAsync(id, token),
        cancellationToken: ct);

    return product is null ? Results.NotFound() : Results.Ok(product);
});

app.Run();
```

---

## See also

- [ASP.NET Core HybridCache documentation](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid)
- [IDistributedCache usage (classic)](../README.md)
- [`ServiceFabricCacheOptions` reference](../Ofn.ServiceFabric.Cache.Client/ServiceFabricCacheOptions.cs)
