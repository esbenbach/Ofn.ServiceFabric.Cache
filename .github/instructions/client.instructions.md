---
applyTo: "Ofn.ServiceFabric.Cache.Client/**/*.cs"
---

# Client-side cache library conventions

## Architecture

- `ServiceFabricDistributedCache` implements `IDistributedCache` and routes keys to SF partitions via `IDistributedCacheStoreLocator`.
- Keys are prefixed with `"{CacheStoreId}-"` before routing. The prefix is cached as `_keyPrefix` in the constructor.
- The locator MD5-hashes the full key to an `Int64`, then resolves against `Int64RangePartitionInformation` to select a partition.
- If `CacheStoreServiceUri` is not configured, `DistributedCacheStoreLocator` auto-discovers the service by walking all cluster services looking for the `"CacheStore" = "true"` property.

## Expiration defaults

- If neither absolute nor sliding expiration is provided, `SetAsync` falls back to `ServiceFabricCacheOptions.DefaultSlidingExpiration` (defaults to 60 seconds).
- If `DefaultSlidingExpiration` is null AND no expiration is given, throw `InvalidOperationException`.

## Metrics pattern

- Client metrics live in `CacheClientMetrics` (meter: `"Ofn.ServiceFabric.Cache.Client"`).
- All metrics are tagged with `cache_store_id`.
- Pre-build `TagList` instances in the constructor from the `_cacheStoreIdString` field — never call `Guid.ToString()` per-operation.

## DI registration

- `ServiceFabricCachingServicesExtensions` provides `AddServiceFabricDistributedCache` extension method on `IServiceCollection`.
- The extension registers `IDistributedCache`, `IDistributedCacheStoreLocator`, and configures `ServiceFabricCacheOptions`.

## Synchronous wrappers

- Sync methods (`Get`, `Set`, `Remove`, `Refresh`) use `Task.Run(() => XxxAsync(...)).GetAwaiter().GetResult()` to avoid deadlocks under ASP.NET Core synchronization contexts. Keep this pattern.
