namespace MethodCache.Testing.Tests;

public interface IProducts
{
    const string ProductTag = "product:{id}";

    [Cache(Duration = "10m", Tags = [ProductTag, "products"])]
    Task<string> GetAsync(int id);

    [Cache(Duration = "10m", Tags = ["prices"])]
    Task<decimal> PriceAsync(int id);
}

public sealed class Products : IProducts
{
    public int Runs;

    public Task<string> GetAsync(int id)
    {
        Interlocked.Increment(ref Runs);
        return Task.FromResult($"product {id}");
    }

    public Task<decimal> PriceAsync(int id) => Task.FromResult(9.5m);
}

public interface IProductWriter
{
    [CacheInvalidate(Tags = ["product:{productId}", "products"])]
    Task RenameAsync(int productId, string name);
}

public sealed class ProductWriter : IProductWriter
{
    public Task RenameAsync(int productId, string name) => Task.CompletedTask;
}

// The price import invalidates these tags
public interface IImportedPrices
{
    [Cache(Duration = "10m", Tags = ["prices", "price:{id}"], InvalidatedElsewhere = ["prices", "price:{id}"])]
    Task<decimal> ImportedPriceAsync(int id);
}

public sealed class ImportedPrices : IImportedPrices
{
    public Task<decimal> ImportedPriceAsync(int id) => Task.FromResult(9.5m);
}
