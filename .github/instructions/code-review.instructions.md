---
applyTo: "Ofn.ServiceFabric.Cache/**/*.cs"
excludeAgent: "cloud-agent"
---

# Code review: server-side invariants

Only flag violations of the rules below. Do not comment on style, formatting, or naming.

## 1. Immutability of state types

`CachedItem` and `CacheStoreMetadata` must remain effectively immutable. Flag:
- New public or internal `set` accessors on any property of these types.
- In-place mutation of an instance read from a Reliable Dictionary (the state manager only persists changes written back via `SetAsync`).
- Removal of the `readonly` modifier from `LinkedDictionaryHelper`.

## 2. Transaction safety

All Reliable Dictionary operations must go through `RetryHelper.ExecuteWithRetry`. Flag:
- Any `StateManager.CreateTransaction()` or `stateManager.CreateTransaction()` call outside of `RetryHelper.cs`.
- Direct `ITransaction` usage that bypasses the retry/backoff/abort logic.

## 3. Hot-path allocation discipline

For code in `BaseCacheStoreService`, `ServiceFabricDistributedCache`, or `DistributedCacheStoreLocator`, flag per-operation allocations that should be cached:
- `Guid.ToString()` called on every request instead of using a cached string field.
- `new TagList { ... }` with constant tags that could be pre-built in the constructor or `OnOpenAsync`.
- `MD5.Create()` per call instead of using the static `MD5.HashData` API with stack-allocated buffers.

## 4. Serializer binary compatibility

Changes to `CachedItemSerializer` or `CacheStoreMetadataSerializer` are dangerous. Flag:
- Any change to field order, field width (e.g., `int` → `long`), or encoding of null sentinels.
- Missing commentary explaining migration strategy (existing replicas must be drained before format changes take effect).

## 5. LinkedDictionaryHelper purity

`LinkedDictionaryHelper` must remain a pure computation — it returns `LinkedDictionaryItemsChanged` without writing to the state manager. Flag:
- Any `SetAsync`, `TryRemoveAsync`, or other write call added directly inside this struct.
- Any injected dependency beyond the `Func<string, Task<ConditionalValue<CachedItem>>>` read delegate.
