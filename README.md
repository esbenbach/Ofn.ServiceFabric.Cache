# Ofn.ServiceFabric.Cache

This repository is just a personal playground for creating a distributed cache using service fabric.
I have started from the great work already done by others at SoCreate https://github.com/SoCreate/service-fabric-distributed-cache


## What is it?
A caching service on service fabric with an IDistributedCache .netstandard implementation using said service.

## How to use it?

Two things is required to start using the cache, a cache store/server which hosts the cached values, and a client that consumes and adds data to the cache.

### Setting up a cache store

1. Create a new .NET Core Stateful Service
2. Add a reference to the Ofn.ServiceFabric.Cache package
3. Either change the auto-generated class or add a new one that inherits from `BaseCacheStoreService` like so
```cs
    internal sealed class CacheHost : BaseCacheStoreService
    {
        public CacheHost(StatefulServiceContext context, ILogger<ICacheStoreService> logger)
            : base(context, logger: logger)
        { }
    }
```

Refer to the `CachingService` project for a more complete example.

### Using the cache store

1. Create any type of .NET Core service fabric application.
2. Add a reference to the Ofn.ServiceFabric.Cache.Client package
3. Configure the .net core container in startup (or a similar location):

```cs
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddDistributedServiceFabricCache();
        }
```

4. Let something consume an `IDistributedCache` and use it as you would any instance of it, like the following ValuesController

```cs
    public class ValuesController : ControllerBase
    {
        private readonly IDistributedCache cache;

        public ValuesController(IDistributedCache cache) {
            this.cache = cache;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<string>>> Get() {
            var values = ( await this.cache.GetAsync("Values") ).FromByteArray<List<string>>();
            return values ?? new List<string>();
        }
    }
```

Refer to the `CacheConsumer` project for a more complete example.


## Why not just contribute to SoCreate Distributed Cache?

Well there is only really one reason, I stumpled upon the SoCreate distributed cache while doing stuff at work, and found it lacked a few minor things I would like to have, but my initial PR was met with "not atm" (understandably, im not pointing fingers here).

I have struggled with caching before, and figured I could have this as my personal pet hobby project, while also getting stuff done at work. 

Maybe something will come of it, maybe not - but there it is, no good reason what so ever - but who said you needed a good reason :-)


[![Build Status](https://dev.azure.com/Ofn/Playground/_apis/build/status/esbenbach.Ofn.ServiceFabric.Cache?branchName=master)](https://dev.azure.com/Ofn/Playground/_build/latest?definitionId=1&branchName=master)

## Contributing
If you want to contribute, feel free to do so, here are some suggestions on how to do that

* Add/Create issues
* Submit Pull Requests that relates to specific issues
* Pull Requests which expands the test coverage
* Pull Requests for a benchmark (redis, sqlserver, service fabric)
* Documentation! (Obviously)