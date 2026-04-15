namespace CacheConsumer.Controllers;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;

/// <summary>
/// Example controller demonstrating <see cref="IDistributedCache"/> usage.
/// </summary>
[Route("api/[controller]")]
[ApiController]
public class ValuesController : ControllerBase
{
    private readonly IDistributedCache cache;

    /// <summary>
    /// Initializes a new <see cref="ValuesController"/>.
    /// </summary>
    /// <param name="cache">The distributed cache used to store and retrieve values.</param>
    public ValuesController(IDistributedCache cache)
    {
        this.cache = cache;
    }

    /// <summary>Returns all stored string values.</summary>
    // GET api/values
    [HttpGet]
    public async Task<ActionResult<IEnumerable<string>>> Get()
    {
        var values = ( await this.cache.GetAsync("Values") )?.FromByteArray<List<string>>();
        return values ?? [];
    }

    /// <summary>Returns the value at the specified zero-based <paramref name="id"/>.</summary>
    /// <param name="id">Zero-based index of the value to retrieve.</param>
    // GET api/values/5
    [HttpGet("{id}")]
    public async Task<ActionResult<string>> Get(int id)
    {
        var values = ( await this.cache.GetAsync("Values") )?.FromByteArray<List<string>>();
        if (id < values?.Count)
        {
            return values.ElementAt(id);
        }

        return NotFound();
    }

    /// <summary>Appends a new string <paramref name="value"/> to the list.</summary>
    /// <param name="value">The string value to append.</param>
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

    /// <summary>Replaces the value at index <paramref name="id"/> with <paramref name="value"/>.</summary>
    /// <param name="id">Zero-based index of the value to replace.</param>
    /// <param name="value">The new string value.</param>
    // PUT api/values/5
    [HttpPut("{id}")]
    public async Task Put(int id, [FromBody] string value)
    {
        var values = ( await this.cache.GetAsync("Values") )?.FromByteArray<List<string>>();
        if (id < values?.Count)
        {
            values[id] = value;
            await this.cache.SetAsync("Values", values.ToByteArray()!);
        }
    }

    /// <summary>Removes the value at index <paramref name="id"/>.</summary>
    /// <param name="id">Zero-based index of the value to remove.</param>
    // DELETE api/values/5
    [HttpDelete("{id}")]
    public async Task Delete(int id)
    {
        var values = ( await this.cache.GetAsync("Values") )?.FromByteArray<List<string>>();
        if (id < values?.Count)
        {
            values.RemoveAt(id);
            await this.cache.SetAsync("Values", values.ToByteArray()!);
        }
    }
}
