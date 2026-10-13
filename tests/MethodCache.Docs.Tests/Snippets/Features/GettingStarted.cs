using MethodCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Docs.GettingStarted
{
    public sealed record Product(int Id, string Name, decimal Price);

    public sealed class Database
    {
        public Task<Product> LoadProductAsync(int id, CancellationToken ct) => Task.FromResult(new Product(id, "Tea", 4.5m));

        public Task<List<Product>> SearchAsync(string text, int page) => Task.FromResult(new List<Product>());

        public Task SaveAsync(Product product) => Task.CompletedTask;
    }

    // begin-snippet: getting-started-before
    public sealed class ProductService(Database db)
    {
        public Task<Product> GetAsync(int id, CancellationToken ct = default) => db.LoadProductAsync(id, ct);

        public Task<List<Product>> SearchAsync(string text, int page) => db.SearchAsync(text, page);

        public Task UpdateAsync(int id, Product product) => db.SaveAsync(product);
    }
    // end-snippet
}

namespace Docs.GettingStarted.After
{
    // begin-snippet: getting-started-interface
    using MethodCache;

    public interface IProductService
    {
        [Cache(Duration = "00:10:00", Tags = ["products", "product:{id}"])]
        Task<Product> GetAsync(int id, CancellationToken ct = default);

        [Cache(Duration = "00:01:00", Tags = ["products"])]
        Task<List<Product>> SearchAsync(string text, int page);

        [CacheInvalidate(Tags = ["products", "product:{id}"])]
        Task UpdateAsync(int id, Product product);
    }

    public sealed class ProductService(Database db) : IProductService
    {
        public Task<Product> GetAsync(int id, CancellationToken ct = default) => db.LoadProductAsync(id, ct);

        public Task<List<Product>> SearchAsync(string text, int page) => db.SearchAsync(text, page);

        public Task UpdateAsync(int id, Product product) => db.SaveAsync(product);
    }
    // end-snippet

    public static class Setup
    {
        public static void Register(WebApplicationBuilder builder)
        {
            // begin-snippet: getting-started-registration
            builder.Services.AddSingleton<Database>();
            builder.Services.AddScoped<IProductService, ProductService>();   // any lifetime
            builder.Services.AddMethodCache();                               // after your registrations
            // end-snippet
        }

        public static void RegisterWithFactory(WebApplicationBuilder builder)
        {
            // begin-snippet: getting-started-factory
            builder.Services.AddMethodCache();
            builder.Services.AddIProductServiceWithCachingScoped(
                sp => new ProductService(sp.GetRequiredService<Database>()));
            // end-snippet
        }
    }

    public interface IShapes
    {
        // begin-snippet: getting-started-shapes
        [Cache] Product Get(int id);                          // synchronous
        [Cache] Task<Product> GetAsync(int id);               // Task<T>
        [Cache] ValueTask<Product> GetFastAsync(int id);      // ValueTask<T>
        [Cache] Task<Product?> FindAsync(string name);        // null results are cached too
        // end-snippet
    }
}
