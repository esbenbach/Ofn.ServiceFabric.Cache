using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Ofn.ServiceFabric.Cache.Client;

namespace CacheConsumer;

public class Startup
{
    public Startup(IConfiguration configuration) => this.Configuration = configuration;

    public IConfiguration Configuration { get; }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddDistributedServiceFabricCache();
    }

    public void Configure(IApplicationBuilder app)
    {
        app.UseDeveloperExceptionPage();
    }
}
