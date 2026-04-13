namespace CacheConsumer.Controllers;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;

[Route("api/[controller]")]
[ApiController]
public class ValuesController : ControllerBase
{
    private readonly IDistributedCache cache;

    public ValuesController(IDistributedCache cache)
    {
        this.cache = cache;
    }

    // GET api/values
    [HttpGet]
    public async Task<ActionResult<IEnumerable<string>>> Get()
    {
        var values = ( await this.cache.GetAsync("Values") )?.FromByteArray<List<string>>();
        return values ?? [];
    }

    // GET api/values/5
    [HttpGet("{id}")]
    public async Task<ActionResult<string>> Get(int id)
    {
        var values = ( await this.cache.GetAsync("Values") )?.FromByteArray<List<string>>();
        if (values?.Count > id+1)
        {
            return values.ElementAt(id);
        }

        return NotFound();
    }

    // POST api/values
    [HttpPost]
    public async Task Post([FromBody] string value)
    {
        var values = (await this.cache.GetAsync("Values"))?.FromByteArray<List<string>>();
        if (values == null)
        {
            values = [];
        }

        values.Add(value);
        await this.cache.SetAsync("Values", values.ToByteArray()!);
    }

    // PUT api/values/5
    [HttpPut("{id}")]
    public async Task Put(int id, [FromBody] string value)
    {
        var values = ( await this.cache.GetAsync("Values") )?.FromByteArray<List<string>>();
        if (values?.Count > id + 1)
        {
            values[id] = value;
            await this.cache.SetAsync("Values", values.ToByteArray()!);
        }
    }

    // DELETE api/values/5
    [HttpDelete("{id}")]
    public async Task Delete(int id)
    {
        var values = ( await this.cache.GetAsync("Values") )?.FromByteArray<List<string>>();
        if (values?.Count > id + 1)
        {
            values.RemoveAt(id);
            await this.cache.SetAsync("Values", values.ToByteArray()!);
        }
    }
}
