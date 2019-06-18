using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.ServiceFabric.Services.Runtime;
using Ofn.ServiceFabric.Cache.Abstractions;

namespace CacheHost
{
    internal static class Program
    {
        /// <summary>
        /// This is the entry point of the service host process.
        /// </summary>
        private static async Task Main()
        {
            try
            {
                // The ServiceManifest.XML file defines one or more service type names.
                // Registering a service maps a service type name to a .NET type.
                // When Service Fabric creates an instance of this service type,
                // an instance of the class is created in this host process.
                var provider = new ServiceCollection()
                        .AddLogging(logging =>
                        {
                            logging.AddConsole();
                            logging.AddDebug();
                            logging.AddEventSourceLogger();
                            logging.SetMinimumLevel(LogLevel.Trace);
                        }).BuildServiceProvider();

                await ServiceRuntime.RegisterServiceAsync("CacheHostType", context => new CacheHost(context, provider.GetService<ILogger<ICacheStoreService>>()));

                ServiceEventSource.Current.ServiceTypeRegistered(Process.GetCurrentProcess().Id, typeof(CacheHost).Name);

                // Prevents this host process from terminating so services keep running.
                Thread.Sleep(Timeout.Infinite);
            }
            catch (Exception e)
            {
                ServiceEventSource.Current.ServiceHostInitializationFailed(e.ToString());
                throw;
            }
        }
    }
}
