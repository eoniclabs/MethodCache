using MethodCache;

namespace MethodCache.BlazorWasmSmokeTest;

public sealed record Product(int Id, string Name, int Load);

public interface IProducts
{
    [Cache(Duration = "10m", Tags = ["products", "product:{id}"])]
    Task<Product> GetAsync(int id, CancellationToken ct = default);

    [Cache(Duration = "10m", RefreshAhead = "1m", Tags = ["products"])]
    Task<Product> GetFreshAsync(int id);

    [CacheInvalidate(Tags = ["products", "product:{id}"])]
    Task UpdateAsync(int id);
}

/// <summary>Counts the loads, so the smoke test can tell a hit from a load.</summary>
public sealed class Loads
{
    public int Count;
}

public sealed class Products(Loads loads) : IProducts
{
    public async Task<Product> GetAsync(int id, CancellationToken ct = default)
    {
        // Yields to the browser, as a call to an API would, so concurrent callers really overlap
        await Task.Yield();
        return new Product(id, "P" + id, Interlocked.Increment(ref loads.Count));
    }

    public async Task<Product> GetFreshAsync(int id)
    {
        await Task.Yield();
        return new Product(id, "F" + id, Interlocked.Increment(ref loads.Count));
    }

    public Task UpdateAsync(int id) => Task.CompletedTask;
}
