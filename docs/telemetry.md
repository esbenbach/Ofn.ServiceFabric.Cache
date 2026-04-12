# Telemetry — Metrics Reference

Both the server-side cache store library and the client library emit metrics via [`System.Diagnostics.Metrics`](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics). Instruments follow the OpenTelemetry semantic conventions for naming and units.

---

## Subscribing to metrics

### OpenTelemetry (recommended)

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("Ofn.ServiceFabric.Cache")        // server-side (if hosting the store in-process)
        .AddMeter("Ofn.ServiceFabric.Cache.Client") // client-side
        .AddPrometheusExporter()                    // or any other exporter
    );
```

### dotnet-counters (ad-hoc)

```bash
dotnet-counters monitor --counters Ofn.ServiceFabric.Cache,Ofn.ServiceFabric.Cache.Client --process-id <pid>
```

---

## Server — `Ofn.ServiceFabric.Cache`

Emitted by `BaseCacheStoreService`. Each instrument is tagged with `partition_id` so metrics from multi-partition deployments can be aggregated or filtered independently.

### `cache.gets` — Counter `{operations}`

Incremented on every `GetCachedItemAsync` call.

| Tag | Values | Description |
|---|---|---|
| `result` | `hit` | Key found and not expired |
| `result` | `expired` | Key found but had expired (item removed, miss returned) |
| `result` | `miss` | Key not present |
| `partition_id` | `<guid>` | SF partition ID |

---

### `cache.operation.duration` — Histogram `ms`

End-to-end latency of each store-side cache operation (includes transaction time).

| Tag | Values | Description |
|---|---|---|
| `operation` | `get`, `set`, `remove` | Which operation was measured |
| `partition_id` | `<guid>` | SF partition ID |

---

### `cache.transaction.retries` — Counter `{retries}`

Incremented each time a Reliable Dictionary transaction is retried after a `TimeoutException`.

| Tag | Values | Description |
|---|---|---|
| `operation` | `get`, `set`, `remove`, `prune` | Operation that retried |
| `partition_id` | `<guid>` | SF partition ID |

---

### `cache.transaction.failures` — Counter `{failures}`

Incremented when a transaction exhausts all retry attempts and throws.

| Tag | Values | Description |
|---|---|---|
| `operation` | `get`, `set`, `remove`, `prune` | Operation that failed |
| `partition_id` | `<guid>` | SF partition ID |

---

### `cache.evictions` — Counter `{items}`

Incremented for each item removed during a background pruning cycle.

| Tag | Values | Description |
|---|---|---|
| `reason` | `lru` | Item evicted because partition exceeded size limit |
| `reason` | `expired` | Item evicted because it had passed its expiry time |
| `partition_id` | `<guid>` | SF partition ID |

---

### `cache.pruning.cycles` — Counter `{cycles}`

Incremented each time the background pruning loop runs (once per `CachePruningInterval`).

| Tag | Values | Description |
|---|---|---|
| `partition_id` | `<guid>` | SF partition ID |

---

### `cache.item.size` — Histogram `By` (bytes)

Records the byte length of each value written via `SetCachedItemAsync`.

| Tag | Values | Description |
|---|---|---|
| `partition_id` | `<guid>` | SF partition ID |

---

### `cache.size.bytes` — ObservableGauge `By` (bytes)

Current total size of all cached values in this partition. Polled on each metrics collection cycle.

| Tag | Values | Description |
|---|---|---|
| `partition_id` | `<guid>` | SF partition ID |

---

### `cache.size.limit.bytes` — ObservableGauge `By` (bytes)

Per-partition size limit (`MaxCacheSize / partitionCount`). Polled on each metrics collection cycle.

| Tag | Values | Description |
|---|---|---|
| `partition_id` | `<guid>` | SF partition ID |

---

## Client — `Ofn.ServiceFabric.Cache.Client`

Emitted by `ServiceFabricDistributedCache` and `DistributedCacheStoreLocator`. Most instruments are tagged with `cache_store_id` to support multiple logical caches backed by the same store service.

### `cache.client.operation.duration` — Histogram `ms`

Client-side end-to-end latency per cache operation (includes network round-trip to the SF service).

| Tag | Values | Description |
|---|---|---|
| `operation` | `get`, `set`, `remove` | Which operation was measured |
| `status` | `success`, `error` | Whether the operation completed without exception |
| `cache_store_id` | `<guid>` | Logical cache namespace (`ServiceFabricCacheOptions.CacheStoreId`) |

---

### `cache.client.gets` — Counter `{operations}`

Incremented on every `GetAsync` / `RefreshAsync` call.

| Tag | Values | Description |
|---|---|---|
| `result` | `hit` | Non-null value returned from the store |
| `result` | `miss` | Null returned (key absent or expired server-side) |
| `cache_store_id` | `<guid>` | Logical cache namespace |

---

### `cache.client.value.size` — Histogram `By` (bytes)

Records the byte length of the value passed to `SetAsync` before it is sent to the store.

| Tag | Values | Description |
|---|---|---|
| `cache_store_id` | `<guid>` | Logical cache namespace |

---

### `cache.client.default_expiration_applied` — Counter `{operations}`

Incremented each time `SetAsync` is called without any expiration and the `DefaultSlidingExpiration` fallback is applied automatically.

| Tag | Values | Description |
|---|---|---|
| `cache_store_id` | `<guid>` | Logical cache namespace |

> A high value here may indicate callers that are not setting explicit expirations. Set `ServiceFabricCacheOptions.DefaultSlidingExpiration = null` to make this a hard error instead.

---

### `cache.client.discovery.duration` — Histogram `ms`

Latency of the automatic cache store service URI discovery (walking the SF cluster to find a service with the `CacheStore=true` property). Only recorded when `CacheStoreServiceUri` is not configured explicitly.

| Tag | Values | Description |
|---|---|---|
| `status` | `success`, `failed` | Whether discovery found a service |

---

### `cache.client.discovery.failures` — Counter `{failures}`

Incremented each time automatic service discovery fails to find a cache store service.

_No tags._

---

### `cache.client.partition_list.refresh.duration` — Histogram `ms`

Latency of fetching the SF partition list used for consistent key-to-partition routing. Recorded each time the cached partition list is refreshed.

_No tags._
