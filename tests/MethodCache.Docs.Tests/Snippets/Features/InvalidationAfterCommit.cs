using MethodCache;
using Microsoft.EntityFrameworkCore;

namespace Docs.InvalidationAfterCommit
{
    public sealed class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
    {
        public DbSet<Product> Products => Set<Product>();
    }

    public interface IProductReads
    {
        const string ProductTag = "product:{id}";

        [Cache(Duration = "00:10:00", Tags = [ProductTag])]
        Task<string?> GetNameAsync(int id);
    }

    public static class ProductEndpoints
    {
        // begin-snippet: invalidation-after-commit
        public static async Task RenameAsync(int id, string name, AppDb db, ICacheManager cache, IMethodCacheKeys keys,
            CancellationToken ct)
        {
            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                var product = await db.Products.SingleAsync(p => p.Id == id, ct);
                product.Name = name;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }

            // After the commit, and not cancelled with the request: the data has changed either way
            await cache.InvalidateTagAsync(
                keys.Tag<IProductReads>(r => r.GetNameAsync(id), IProductReads.ProductTag), CancellationToken.None);
        }
        // end-snippet
    }
}
