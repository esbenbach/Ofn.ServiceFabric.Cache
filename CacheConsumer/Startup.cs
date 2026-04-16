using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Ofn.ServiceFabric.Cache.Client;

namespace CacheConsumer;

/// <summary>ASP.NET Core startup configuration for the CacheConsumer example application.</summary>
public class Startup
{
    /// <summary>Initializes a new <see cref="Startup"/> with the supplied configuration.</summary>
    /// <param name="configuration">The application configuration.</param>
    public Startup(IConfiguration configuration) => this.Configuration = configuration;

    /// <summary>The application configuration provided by the host.</summary>
    public IConfiguration Configuration { get; }

    /// <summary>Registers application services with the DI container.</summary>
    /// <param name="services">The service collection to configure.</param>
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddDistributedServiceFabricCache();
    }

    /// <summary>Configures the HTTP request pipeline.</summary>
    /// <param name="app">The application builder used to configure middleware.</param>
    public void Configure(IApplicationBuilder app)
    {
        app.UseDeveloperExceptionPage();
    }
}
