# Copilot Instructions

## Build & Test

```sh
dotnet restore
dotnet build
dotnet test
```

Run a single test class or method:
```sh
dotnet test test/Ofn.ServiceFabric.Cache.UnitTests --filter "FullyQualifiedName~BaseCacheStoreServiceTest"
dotnet test test/Ofn.ServiceFabric.Cache.UnitTests --filter "FullyQualifiedName~BaseCacheStoreServiceTest.GetCachedItemAsync_GetItemThatDoesNotExist_NullResultReturned"
```

Pack NuGet packages (no build):
```sh
dotnet pack --no-build
```

## Architecture

This is a distributed cache implemented on top of Azure Service Fabric Reliable Services, exposing a standard `IDistributedCache` interface.

### Project Layout

| Project | Role |
|---|---|
| `Ofn.ServiceFabric.Cache.Abstractions` | Shared interfaces (`ICacheStoreService`, `IDistributedCacheStoreLocator`). Referenced by both server and client. |
| `Ofn.ServiceFabric.Cache` | **Server-side library.** `BaseCacheStoreService` is an abstract `StatefulService` that hosts the cache in SF Reliable Dictionaries. |
| `Ofn.ServiceFabric.Cache.Client` | **Client-side library.** `ServiceFabricDistributedCache` implements `IDistributedCache`. `DistributedCacheStoreLocator` finds the cache service via SF remoting. |
| `Ofn.ServiceFabric.Cache.Hosting` | SF application project (`.sfproj`) that hosts the cache store. |
| `CachingService` | Example implementation of the cache store (extends `BaseCacheStoreService`). |
| `CacheConsumer` | Example ASP.NET Core app consuming `IDistributedCache`. |
| `test/Ofn.ServiceFabric.Cache.UnitTests` | Unit tests (xUnit + AutoFixture + Moq). |

### Data Model & LRU Ordering

Each SF partition holds **two Reliable Dictionaries**:
- `CacheStore` (`string → CachedItem`): the cached values.
- `CacheStoreMetadata` (`string → CacheStoreMetadata`): a single metadata record keyed by `CacheStoreConstants.CacheStoreMetadataKey` that tracks total size, `FirstCacheKey`, and `LastCacheKey`.

`CachedItem` embeds `BeforeCacheKey` and `AfterCacheKey` fields, forming a **doubly-linked list across dictionary entries** for LRU tracking. `LinkedDictionaryHelper` encapsulates all mutations to this linked list; it never writes directly—it returns a `LinkedDictionaryItemsChanged` result that `BaseCacheStoreService` applies via `ApplyChanges`.

### Partitioning & Key Routing

Cache keys are routed to SF partitions by MD5-hashing the key to an `Int64`, then resolving against `Int64RangePartitionInformation`. Client-side keys are prefixed with `CacheStoreId` (a `Guid` from `ServiceFabricCacheOptions`) to allow multiple logical caches on a single store service.

### Cache Store Discovery

On `OnOpenAsync`, the server sets a FabricClient property `"CacheStore" = "true"` on its service URI. If the client has no `CacheStoreServiceUri` configured, `DistributedCacheStoreLocator` walks all applications/services in the cluster looking for this property.

### Reliable Dictionary Transactions

All reads and writes go through `RetryHelper.ExecuteWithRetry`, which opens a transaction, runs the operation, commits, and retries with exponential backoff on `TimeoutException`. All callers must use this helper—never create transactions manually.

### Expiration Behavior

- If neither sliding nor absolute expiration is provided by the caller, a **60-second sliding expiration is applied automatically** (`ServiceFabricDistributedCache.SetAsync`).
- Expiry is **lazy on read**: expired items are removed when accessed, not on a timer.
- A background loop in `RunAsync` calls `RemoveLeastRecentlyUsedCacheItemWhenOverMaxSize` every `CachePruningInterval` seconds to evict expired/LRU items when the partition exceeds `MaxCacheSize / partitionCount` bytes.

### Custom Serializers

`CachedItem` and `CacheStoreMetadata` each have a custom `IStateSerializer<T>` registered in `BaseCacheStoreService`'s constructor. Any new Reliable Dictionary state type must register its own serializer the same way.

## Key Conventions

- **`CachedItem` is immutable.** All modifications create a new instance. Never add mutable setters.
- **`LinkedDictionaryHelper` is pure.** It returns change sets (`LinkedDictionaryItemsChanged`) without touching the state manager. Keep it that way.
- **`Shared.targets`** is imported by all NuGet-published `.csproj` files for shared package metadata (author, license, version).
- **Tests use `[Theory, AutoMoqData]`** (AutoFixture + AutoMoq). Use `[Frozen]` to inject shared mocks, `[Greedy]` to select the most-parameterised constructor of the SUT. No SF cluster is needed—`IReliableStateManagerReplica2` is mocked.
- **`ICacheStoreService` extends `IService`** (SF remoting marker interface). Any changes to its method signatures require regenerating the remoting proxy.
