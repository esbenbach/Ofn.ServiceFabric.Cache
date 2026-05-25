---
applyTo: "CachingService/**/*.cs,CacheConsumer/**/*.cs"
---

# Example service conventions

These are example/sample implementations demonstrating how to host and consume the cache library within a Service Fabric application.

## CachingService (server host)

- `CacheHost` is a minimal subclass of `BaseCacheStoreService` that only provides a constructor. Keep it thin.
- `Program.cs` registers the service with `ServiceRuntime`.
- `ServiceEventSource` is the ETW event source for the SF host process.
- Configuration lives in `PackageRoot/Config/Settings.xml` and `PackageRoot/ServiceManifest.xml`.

## CacheConsumer (client host)

- An ASP.NET Core stateless service that consumes `IDistributedCache`.
- Uses `ServiceFabricCachingServicesExtensions.AddServiceFabricDistributedCache` for DI setup.
- `ValuesController` demonstrates basic Get/Set via the distributed cache.
- `SerializationExtension` uses **MemoryPack** for payload serialization (separate from the Reliable Dictionary serializers).
- Uses Kestrel as the web server (`Microsoft.ServiceFabric.AspNetCore.Kestrel`).
