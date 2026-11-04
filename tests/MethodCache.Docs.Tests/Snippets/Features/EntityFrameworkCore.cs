using MethodCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Docs.EntityFrameworkCore
{
    public sealed class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
        public int CategoryId { get; set; }
    }

    public sealed class OrderLine
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public int ProductId { get; set; }
        public int Quantity { get; set; }
    }

    public sealed class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options)
    {
        public DbSet<Product> Products => Set<Product>();
        public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    }

    public record ProductDto(int Id, string Name, decimal Price);

    // begin-snippet: efcore-reads
    public interface IProductReads
    {
        [Cache(Duration = "00:10:00", Tags = ["product:{id}"])]
        Task<ProductDto?> GetAsync(int id);

        [Cache(Duration = "00:10:00", Tags = ["category:{categoryId}"])]
        Task<IReadOnlyList<ProductDto>> ListAsync(int categoryId);

        [Cache(Duration = "01:00:00", Tags = ["top-sellers"])]
        Task<IReadOnlyList<ProductDto>> TopSellersAsync();
    }
    // end-snippet

    public sealed class ProductReads(ShopDb db) : IProductReads
    {
        public Task<ProductDto?> GetAsync(int id)
            => db.Products.Where(p => p.Id == id).Select(p => new ProductDto(p.Id, p.Name, p.Price)).SingleOrDefaultAsync();

        public async Task<IReadOnlyList<ProductDto>> ListAsync(int categoryId)
            => await db.Products.Where(p => p.CategoryId == categoryId).Select(p => new ProductDto(p.Id, p.Name, p.Price)).ToListAsync();

        public async Task<IReadOnlyList<ProductDto>> TopSellersAsync()
            => await db.Products.OrderBy(p => p.Id).Take(3).Select(p => new ProductDto(p.Id, p.Name, p.Price)).ToListAsync();
    }

    public static class Setup
    {
        public static void Register(WebApplicationBuilder builder, string connectionString)
        {
            // begin-snippet: efcore-register
            builder.Services.AddDbContext<ShopDb>(o => o.UseSqlite(connectionString));
            builder.Services.AddScoped<IProductReads, ProductReads>();

            builder.Services.AddMethodCache(mc => mc.UseEntityFrameworkCore<ShopDb>(ef => ef
                .Entity<Product>("product:{Id}", "category:{CategoryId}")
                .Entity<OrderLine>("top-sellers")));
            // end-snippet
        }
    }

    public static class ProductEndpoints
    {
        // begin-snippet: efcore-save
        public static async Task ChangePriceAsync(int id, decimal price, ShopDb db, CancellationToken ct)
        {
            var product = await db.Products.SingleAsync(p => p.Id == id, ct);
            product.Price = price;
            // Invalidates product:{id} and category:{CategoryId} once the save succeeded
            await db.SaveChangesAsync(ct);
        }
        // end-snippet

        // begin-snippet: efcore-transaction
        public static async Task MoveAsync(int id, int categoryId, ShopDb db, CancellationToken ct)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var product = await db.Products.SingleAsync(p => p.Id == id, ct);
            product.CategoryId = categoryId;
            await db.SaveChangesAsync(ct);   // nothing invalidated yet: the transaction is open
            await transaction.CommitAsync(ct);   // product:{id}, the old and the new category:{CategoryId}
        }
        // end-snippet

        // begin-snippet: efcore-invalidate-after-commit
        public static async Task RecalculateAsync(ShopDb db, IMethodCacheKeys keys, int categoryId, CancellationToken ct)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Products
                .Where(p => p.CategoryId == categoryId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, p => p.Price * 1.1m), ct);
            // ExecuteUpdateAsync goes past the change tracker: name the tags it changes
            db.InvalidateAfterCommit(keys.Tag<IProductReads>(r => r.ListAsync(categoryId), "category:{categoryId}"));
            await transaction.CommitAsync(ct);
        }
        // end-snippet

        // begin-snippet: efcore-committed-write
        public static async Task DeleteCategoryAsync(ShopDb db, ICacheManager cache, IMethodCacheKeys keys, int categoryId,
            CancellationToken ct)
        {
            // Without a transaction the delete is committed when it returns: invalidate at once
            await db.Products.Where(p => p.CategoryId == categoryId).ExecuteDeleteAsync(ct);
            await cache.InvalidateTagAsync(
                keys.Tag<IProductReads>(r => r.ListAsync(categoryId), "category:{categoryId}"), CancellationToken.None);
        }
        // end-snippet
    }
}
