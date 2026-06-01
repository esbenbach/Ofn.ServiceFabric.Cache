---
applyTo: "Ofn.ServiceFabric.Cache/**/*.cs"
---

# Server-side cache store conventions

## Architecture constraints

- `BaseCacheStoreService` is the abstract engine. Subclasses (like `CacheHost`) should only provide constructors and optional configuration — never override the core Get/Set/Remove logic.
- All state manager operations MUST go through `RetryHelper.ExecuteWithRetry`. Never create `ITransaction` instances manually.
- `LinkedDictionaryHelper` is a **pure readonly struct**. It computes change sets (`LinkedDictionaryItemsChanged`) without side effects. Always call `ApplyChanges` to persist its results.
- `CachedItem` and `CacheStoreMetadata` are effectively immutable. Create new instances rather than mutating. Never add mutable setters.

## Serialization

- Each type stored in a Reliable Dictionary requires a custom `IStateSerializer<T>` registered in the `BaseCacheStoreService` constructor via `StateManager.TryAddStateSerializer`.
- Binary format is manual (`BinaryReader`/`BinaryWriter`). Null strings are encoded as empty-string. Null `TimeSpan` is encoded as 0 ticks. Null `DateTimeOffset` is encoded as 0 ticks for both the date data and offset.
- The serializer format is versioned implicitly by field order and width. Changes to the serializer format require draining existing replicas.

## Metrics pattern

- Metrics live in the static `CacheMetrics` class using `System.Diagnostics.Metrics`.
- The meter name is `"Ofn.ServiceFabric.Cache"`.
- All metrics are tagged with `partition_id`. Use the cached `_partitionIdTag` string field, not `Partition.PartitionInfo.Id.ToString()`.
- Observable gauges for size/limit are created in `OnOpenAsync`, not in the constructor.
- Reliable Dictionaries (`_cacheStore`, `_cacheStoreMetadata`) are initialized at the top of `RunAsync`, NOT in `OnOpenAsync` or `OnChangeRoleAsync`. `RunAsync` is only called after the replica is promoted to Primary and the state manager is fully readable. `OnChangeRoleAsync` is called *during* the role transition when the state manager is NOT yet readable — calling `GetOrAddAsync` there causes `FabricNotReadableException`. `OnChangeRoleAsync` only nulls out the dictionary references when demoting from Primary.

## Performance conventions

- Cache `Stopwatch`, `TagList`, `Func<>` delegates, and string representations once in fields — avoid per-call allocations.
- Use `stackalloc` and `Span<T>` for hot-path hashing (MD5).
- Prefer `LockMode.Update` when reading values that will be written back in the same transaction.
- The `RetryHelper` uses bit-shift (`1 << attempts`) for backoff factor, not `Math.Pow`.

## Background loops

`RunAsync` starts two `Task.WhenAll` loops:
1. **LRU pruning** — runs every `CachePruningInterval` seconds.
2. **Expiration scan** — runs every `ExpirationScanInterval` seconds, inspects up to `ExpirationScanBatchSize` items.

Both loops catch non-`OperationCanceledException` exceptions, log them, and continue. Never let exceptions propagate out of these loops unhandled.

## Service Fabric lifecycle

The SF replica lifecycle order is: `OnOpenAsync` → `OnChangeRoleAsync` → `RunAsync` → `OnCloseAsync`. Key constraints:

- `OnOpenAsync`: registers the service property and resolves the partition count. **Do not** touch the state manager here.
- `OnChangeRoleAsync`: only nulls references when demoting. **Do not** call `StateManager.GetOrAddAsync` here — the state manager is not readable during role transition.
- `RunAsync`: only called on the Primary. This is the correct place to call `StateManager.GetOrAddAsync` and start background loops.
- `OnCloseAsync`: always delegates to `base.OnCloseAsync`, then nulls dictionary references.

## Local deployment verification

**Unit tests alone are insufficient** to verify lifecycle correctness — the mock state manager does not enforce SF's readability constraints. After any change to `OnOpenAsync`, `OnChangeRoleAsync`, `RunAsync`, or `OnCloseAsync`, **you must deploy and start `CachingService` against a local SF cluster** (via Visual Studio F5 or the Deploy scripts) to confirm the replica opens and reaches Primary without errors in the SF Event Log.
