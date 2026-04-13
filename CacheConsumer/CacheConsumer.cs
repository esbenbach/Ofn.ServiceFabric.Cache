using System.Fabric;
using Microsoft.ServiceFabric.Services.Communication.AspNetCore;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;

namespace CacheConsumer;

/// <summary>
/// The FabricRuntime creates an instance of this class for each service type instance. 
/// </summary>
internal sealed class CacheConsumer : StatelessService
{
    public CacheConsumer(StatelessServiceContext context)
        : base(context)
    { }

    /// <summary>
    /// Optional override to create listeners (like tcp, http) for this service instance.
    /// </summary>
    /// <returns>The collection of listeners.</returns>
    protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
    {
        return
        [
            new ServiceInstanceListener(serviceContext =>
                new KestrelCommunicationListener(serviceContext, "ServiceEndpoint", (url, listener) =>
                {
                    ServiceEventSource.Current.ServiceMessage(serviceContext, $"Starting Kestrel on {url}");
                    return Host.CreateDefaultBuilder([])
                                .ConfigureWebHostDefaults(cfg =>
                                {
                                        cfg.UseContentRoot(Directory.GetCurrentDirectory())
                                            .UseKestrel()
                                            .UseStartup<Startup>()
                                            .UseServiceFabricIntegration(listener, ServiceFabricIntegrationOptions.None)
                                            .UseUrls(url)
                                            .ConfigureServices(services =>
                                            {
                                                services.AddSingleton(serviceContext);
                                            })
                                            .ConfigureAppConfiguration((hostingContext, config) =>
                                            {
                                                var env = hostingContext.HostingEnvironment;
                                                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                                                      .AddJsonFile($"appsettings.{env.EnvironmentName}.json",
                                                          optional: true, reloadOnChange: true);
                                                config.AddEnvironmentVariables();
                                            })
                                            .ConfigureLogging((hostingContext, logging) =>
                                            {
                                                logging.AddConfiguration(hostingContext.Configuration.GetSection("Logging"));
                                                logging.AddConsole();
                                                logging.AddDebug();
                                                logging.AddEventSourceLogger();
                                            });
                                }).Build();
                }))
        ];
    }
}
