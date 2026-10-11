using System.Text.Json;
using MethodCache.Core;

namespace MethodCache.WebDemo;

public sealed record Product(int Id, string Name, string Category, decimal Price);

public interface IProductCatalog
{
    [Cache(Duration = "00:10:00", Tags = ["products", "product:{id}"])]
    Task<Product?> GetProductAsync(int id);

    [Cache(Duration = "00:02:00", Tags = ["products", "product-lists"])]
    Task<IReadOnlyList<Product>> GetByCategoryAsync(string category);

    // A price change makes this product and the lists stale. Other products stay cached.
    [CacheInvalidate(Tags = ["product:{id}", "product-lists"])]
    Task UpdatePriceAsync(int id, decimal price);
}

/// <summary>
/// Stands in for a database: every query takes 300 ms. The data lives in a file in the temp folder, so two instances
/// of the demo (the "redis-a" and "redis-b" launch profiles) share it like they would share a real database.
/// </summary>
public sealed class ProductCatalog : IProductCatalog
{
    private static readonly TimeSpan QueryTime = TimeSpan.FromMilliseconds(300);
    private static readonly string DataFile = Path.Combine(Path.GetTempPath(), "methodcache-webdemo-products.json");

    private static readonly Product[] Seed =
    [
        new(1, "Mechanical keyboard", "keyboards", 129m),
        new(2, "Low-profile keyboard", "keyboards", 99m),
        new(3, "Ergonomic mouse", "mice", 79m),
        new(4, "4K monitor", "monitors", 449m),
    ];

    public async Task<Product?> GetProductAsync(int id)
    {
        await Task.Delay(QueryTime);
        return (await LoadAsync()).FirstOrDefault(p => p.Id == id);
    }

    public async Task<IReadOnlyList<Product>> GetByCategoryAsync(string category)
    {
        await Task.Delay(QueryTime);
        return (await LoadAsync()).Where(p => p.Category == category).ToList();
    }

    public async Task UpdatePriceAsync(int id, decimal price)
    {
        var products = await LoadAsync();
        var index = Array.FindIndex(products, p => p.Id == id);
        if (index < 0)
        {
            throw new KeyNotFoundException($"No product {id}");
        }

        products[index] = products[index] with { Price = price };
        await File.WriteAllTextAsync(DataFile, JsonSerializer.Serialize(products));
    }

    private static async Task<Product[]> LoadAsync() =>
        File.Exists(DataFile)
            ? JsonSerializer.Deserialize<Product[]>(await File.ReadAllTextAsync(DataFile)) ?? Seed.ToArray()
            : Seed.ToArray();
}
