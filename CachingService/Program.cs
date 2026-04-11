using CacheHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.ServiceFabric.Services.Runtime;
using Ofn.ServiceFabric.Cache.Abstractions;

namespace CachingService;

internal static class Program
{
    private static async Task Main()
    {
        try
        {
            var provider = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider();

            await ServiceRuntime.RegisterServiceAsync("CacheHostType", context => new CacheHost(context, provider.GetRequiredService<ILogger<ICacheStoreService>>()));

            ServiceEventSource.Current.ServiceTypeRegistered(Environment.ProcessId, typeof(CacheHost).Name);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        catch (Exception e)
        {
            ServiceEventSource.Current.ServiceHostInitializationFailed(e.ToString());
            throw;
        }
    }
}
