---
applyTo: "test/**/*.cs"
---

# Unit test conventions

## Framework & libraries

- xUnit 3 (`xunit.v3`) + AutoFixture (`AutoFixture.AutoMoq`, `AutoFixture.Xunit3`) + Moq.
- Tests do NOT require a Service Fabric cluster. `IReliableStateManagerReplica2` is fully mocked.

## Test patterns

- Use `[Theory, AutoMoqData]` for all parameterized tests. `AutoMoqData` is a custom attribute that:
  - Creates a `Fixture` with `AutoMoqCustomization`.
  - Registers `FakeTimeProvider` as the `TimeProvider` implementation.
  - Registers a `StatefulServiceContext` factory that stubs `ICodePackageActivationContext` string properties (required by SF SDK 8.4+ telemetry in `StatefulServiceBase..ctor`).
- Use `[Frozen]` to share mock instances across test parameters.
- Use `[Greedy]` on the SUT parameter to select the most-parameterised constructor.

## Test doubles for `BaseCacheStoreService`

- **`StubCacheStoreService`** (in `BaseCacheStoreServiceTest`): uses hardcoded `MaxCacheSize=1, CachePruningInterval=1`. AutoFixture injects it via `[Greedy]`. Wire stores by passing `stub` as the third argument to `SetupInMemoryStores`.
- **`CustomSettingsStubPublic`** (in `BaseCacheStoreServiceTest`): for tests that need specific `CacheStoreSettings`. Construct manually, then call `InitCacheStore`/`InitCacheStoreMetadata`. Use the 2-arg `SetupInMemoryStores(stateManager, dict)` overload.

## In-memory store setup

The `SetupInMemoryStores` helper method:
1. Creates a `Dictionary<TKey, TValue>` backing store.
2. Mocks `GetOrAddAsync`, `TryGetValueAsync` (both overloads), `SetAsync`, `TryRemoveAsync`.
3. Optionally wires the dictionary into the stub's `_cacheStore` or `_cacheStoreMetadata` field.
4. Returns the in-memory dictionary for assertion access.

Always call it for BOTH the `CachedItem` dictionary AND the `CacheStoreMetadata` dictionary.

## Time control

Inject `[Frozen] FakeTimeProvider timeProvider` and call `timeProvider.SetUtcNow(...)` to control expiration behavior deterministically.

## XML doc comments

The `.editorconfig` suppresses CS1591 for test and benchmark projects — XML docs are NOT required in test code.
