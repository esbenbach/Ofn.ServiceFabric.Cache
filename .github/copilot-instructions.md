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

## Project Settings

All projects target **net10.0** with `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>` (set in `Directory.Build.props`). All package versions are managed centrally in `Directory.Packages.props` (CPM). Do not add `Version` attributes to `<PackageReference>` elements in `.csproj` files.

`TreatWarningsAsErrors` and `GenerateDocumentationFile` are both enabled globally. **All public APIs must have XML doc comments** (`<summary>` at minimum) or the build will fail.

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

- If neither absolute nor sliding expiration is provided, `ServiceFabricDistributedCache.SetAsync` falls back to `ServiceFabricCacheOptions.DefaultSlidingExpiration` (defaults to 60 seconds). If `DefaultSlidingExpiration` is `null`, an `InvalidOperationException` is thrown.
- Expiry is **lazy on read**: expired items are removed when accessed, not on a timer.
- `BaseCacheStoreService` uses `TimeProvider` (not `DateTime.UtcNow`) for all time comparisons, enabling deterministic testing via `FakeTimeProvider`.
- `RunAsync` runs **two independent background loops**:
  1. **LRU pruning** (`CachePruningInterval` seconds): calls `RemoveLeastRecentlyUsedCacheItemWhenOverMaxSize` when the partition exceeds `MaxCacheSize / partitionCount` bytes.
  2. **Expiration scan** (`ExpirationScanInterval` seconds, default 30): calls `RemoveExpiredCacheItemsAsync` proactively, processing up to `ExpirationScanBatchSize` items (default 500) per cycle.

### Custom Serializers

`CachedItem` and `CacheStoreMetadata` each have a custom `IStateSerializer<T>` registered in `BaseCacheStoreService`'s constructor. Any new Reliable Dictionary state type must register its own serializer the same way.

## Key Conventions

- **`CachedItem` is immutable.** All modifications create a new instance. Never add mutable setters.
- **`LinkedDictionaryHelper` is pure.** It returns change sets (`LinkedDictionaryItemsChanged`) without touching the state manager. Keep it that way.
- **`Directory.Build.props`** holds shared package metadata (author, license, version) and reads `PACKAGE_VERSION` from the environment. Local builds default to `0.0.0-local`.
- **Tests use `[Theory, AutoMoqData]`** (AutoFixture + AutoMoq). Use `[Frozen]` to inject shared mocks, `[Greedy]` to select the most-parameterised constructor of the SUT. No SF cluster is needed—`IReliableStateManagerReplica2` is mocked.
- **`StubCacheStoreService`** (inner class in `BaseCacheStoreServiceTest`) is the concrete test double for `BaseCacheStoreService`. Use `[Greedy]` so AutoFixture picks its 4-param ctor `(StatefulServiceContext, IReliableStateManagerReplica2, TimeProvider, ILogger?)`. Call `SetupInMemoryStores` to wire in-memory dictionaries and set the `_cacheStore`/`_cacheStoreMetadata` fields on the stub.
- **`CustomSettingsStubPublic`** (also in `BaseCacheStoreServiceTest`) is used when tests need non-default `CacheStoreSettings`. Construct it manually and call `InitCacheStore`/`InitCacheStoreMetadata` to wire the in-memory dictionaries; use the 2-arg `SetupInMemoryStores(stateManager, dict)` overload (without the stub arg) in this case.
- **`AutoMoqData` registers `FakeTimeProvider`** as the fixture implementation of `TimeProvider`. Inject `[Frozen] FakeTimeProvider` into tests to control the clock.
- **SF SDK 8.4+ telemetry**: `StatefulServiceBase..ctor` calls telemetry that reads `ICodePackageActivationContext` string properties. `AutoMoqData` sets these up with non-null stubs; replicate this when building `StatefulServiceContext` outside the fixture.
- **`ICacheStoreService` extends `IService`** (SF remoting marker interface). Any changes to its method signatures require regenerating the remoting proxy.
- **Item size accounting**: each item's reported size is `value.Length + ByteSizeOffset`. `ByteSizeOffset` defaults to 250 (configurable via `CacheStoreSettings.ByteSizeOffset`). Tests that assert on `metadata.Size` must account for this offset.

## CI/CD

The project uses **GitHub Actions** (`.github/workflows/ci-release.yml`) for continuous integration and release publishing.

### Pipeline Jobs

| Job | Trigger | What it does |
|-----|---------|--------------|
| **CI** | Push to `main` | Build → test → pack with CalVer-preview → push to GitHub Packages |
| **Release** | Manual approval (environment gate) | Rebuild without `-preview` → push to NuGet.org → create GitHub Release |

### Versioning

- Format: `yyyy.M.d.{run_number}` (CalVer). CI appends `-preview`.
- `PACKAGE_VERSION` env var is set by the workflow; `Directory.Build.props` reads it.
- Local builds produce `0.0.0-local` by default.

### Authentication & Environments

- **NuGet.org** uses **Trusted Publishing** (OIDC via `NuGet/login@v1`) — no API key secret needed.
- `NuGet-Release` environment with required reviewer gate for the Release job.
- `GITHUB_TOKEN` (built-in) for GitHub Packages.

### Legacy

`Deploy/azure-pipelines.yml` is an Azure DevOps pipeline kept for reference. It is deprecated in favour of GitHub Actions.
