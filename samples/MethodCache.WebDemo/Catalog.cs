namespace MethodCache.WebDemo;

public sealed record Product(int Id, string Name, string Category, decimal Price);

public interface IProductCatalog
{
    [Cache(Duration = "00:10:00", Tags = ["products", "product:{id}"])]
    Task<Product?> GetProductAsync(int id);

    [Cache(Duration = "00:02:00", Tags = ["products", "product-lists"])]
    Task<IReadOnlyList<Product>> GetByCategoryAsync(string category);

    [CacheInvalidate(Tags = ["product:{id}", "product-lists"])]
    Task UpdatePriceAsync(int id, decimal price);

    [CacheInvalidate(Tags = ["products"])]
    Task ResetAsync();
}

/// <summary>SQLite is shared by both demo instances. Count real reads to distinguish L1/L2 hits from database queries.</summary>
public sealed class ProductCatalog(ProductStore store, IConfiguration configuration, TimeProvider clock) : IProductCatalog
{
    private readonly TimeSpan _queryTime = TimeSpan.FromMilliseconds(configuration.GetValue("Demo:QueryDelayMs", 300));
    private int _queries;
    public int Queries => Volatile.Read(ref _queries);

    public async Task<Product?> GetProductAsync(int id)
    {
        var latency = Task.Delay(_queryTime, clock);
        Interlocked.Increment(ref _queries);
        await latency;
        return await store.GetAsync(id);
    }

    public async Task<IReadOnlyList<Product>> GetByCategoryAsync(string category)
    {
        var latency = Task.Delay(_queryTime, clock);
        Interlocked.Increment(ref _queries);
        await latency;
        return await store.GetByCategoryAsync(category);
    }

    public async Task UpdatePriceAsync(int id, decimal price)
    {
        if (!await store.UpdatePriceAsync(id, price)) throw new KeyNotFoundException($"No product {id}");
    }

    public async Task ResetAsync()
    {
        await store.ResetAsync();
        Interlocked.Exchange(ref _queries, 0);
    }
}
