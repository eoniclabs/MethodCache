using MethodCache.WebDemo;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Samples.Tests;

[CollectionDefinition("Product catalog", DisableParallelization = true)]
public class ProductCatalogCollection;

[Collection("Product catalog")]
public class ProductCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "methodcache-catalog-tests-" + Guid.NewGuid());
    private readonly FakeTimeProvider _time = new();

    public ProductCatalogTests() => Directory.CreateDirectory(_directory);
    private string DataFile => Path.Combine(_directory, "products.db");

    [Fact]
    public async Task ConcurrentPriceUpdates_IndependentCatalogs_PreserveEveryChange()
    {
        var catalog = new ProductCatalog(DataFile, _time);
        await catalog.UpdatePriceAsync(1, 129m);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = Enumerable.Range(1, 4).Select(async id =>
        {
            await ready.Task;
            await new ProductCatalog(DataFile, _time).UpdatePriceAsync(id, 1000m + id);
        }).ToArray();
        ready.SetResult();
        await Task.WhenAll(updates);

        var reads = Enumerable.Range(1, 4).Select(catalog.GetProductAsync).ToArray();
        _time.Advance(TimeSpan.FromMilliseconds(300));
        var products = await Task.WhenAll(reads);
        Assert.All(products, product => Assert.Equal(1000m + product!.Id, product.Price));
    }

    [Fact]
    public async Task Catalog_QueriesAndUpdates_PreserveDecimalValues()
    {
        var catalog = new ProductCatalog(DataFile, _time);
        await catalog.UpdatePriceAsync(2, 123.4567890123456789012345678m);
        var query = catalog.GetByCategoryAsync("keyboards");
        _time.Advance(TimeSpan.FromMilliseconds(300));
        var products = await query;
        Assert.Equal(2, products.Count);
        Assert.Equal(123.4567890123456789012345678m, products.Single(product => product.Id == 2).Price);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => catalog.UpdatePriceAsync(999, 1m));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
