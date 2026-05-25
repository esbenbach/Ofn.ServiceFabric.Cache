---
applyTo: "Ofn.ServiceFabric.Cache.Abstractions/**/*.cs"
---

# Abstractions project conventions

- This project defines the **shared contracts** between server and client. It is referenced by both.
- `ICacheStoreService` extends `IService` (SF remoting marker interface). Any method signature change requires regenerating the SF remoting proxy and is a **breaking change** for all consumers.
- `IDistributedCacheStoreLocator` is the client-side abstraction for resolving a cache store proxy by key.
- Keep this project minimal — only interfaces and simple DTOs. No implementation logic.
- The project only references `Microsoft.ServiceFabric.Services.Remoting`.
