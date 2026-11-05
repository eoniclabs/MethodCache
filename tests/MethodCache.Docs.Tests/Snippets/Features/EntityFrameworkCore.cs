using MethodCache;
using MethodCache.EntityFrameworkCore;
using MethodCache.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

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
        public int? CustomerId { get; set; }
    }

    public sealed class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public DateTime LastSignInAt { get; set; }
    }

    public sealed class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options)
    {
        public DbSet<Product> Products => Set<Product>();
        public DbSet<OrderLine> OrderLines => Set<OrderLine>();
        public DbSet<Customer> Customers => Set<Customer>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            // Deleting a customer deletes their order lines in the database (ON DELETE CASCADE)
            => modelBuilder.Entity<OrderLine>().HasOne<Customer>().WithMany().HasForeignKey(l => l.CustomerId).OnDelete(DeleteBehavior.Cascade);
    }

    public record ProductDto(int Id, string Name, decimal Price);

    // begin-snippet: efcore-reads
    public interface IProductReads
    {
        [Cache(Duration = "10m", Tags = ["product:{id}"])]
        Task<ProductDto?> GetAsync(int id);

        [Cache(Duration = "10m", Tags = ["category:{categoryId}"])]
        Task<IReadOnlyList<ProductDto>> ListAsync(int categoryId);

        [Cache(Duration = "1h", Tags = ["top-sellers"])]
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

        public static void RegisterWithCustomers(WebApplicationBuilder builder, string connectionString)
        {
            builder.Services.AddDbContext<ShopDb>(o => o.UseSqlite(connectionString));
            builder.Services.AddScoped<IProductReads, ProductReads>();

            // begin-snippet: efcore-when-deleted
            builder.Services.AddMethodCache(mc => mc.UseEntityFrameworkCore<ShopDb>(ef => ef
                .Entity<Product>("product:{Id}", "category:{CategoryId}")
                .Entity<OrderLine>("top-sellers")
                // Every sign-in saves the customer, and changes no cached result. A deleted customer does: the
                // database deletes their order lines, which the context never loaded
                .Entity<Customer>(customer => customer.WhenDeleted("top-sellers"))));
            // end-snippet
        }
    }

    public static class MappingTests
    {
        public static void CheckTags(IServiceProvider provider)
        {
            // begin-snippet: efcore-tag-coverage
            var mapping = provider.GetRequiredService<EntityTagMapping<ShopDb>>();
            // [(Product, ["product:{Id}", "category:{CategoryId}"], []), (OrderLine, ["top-sellers"], [])]
            var entities = mapping.Entities;

            // Every tag of a cached method is mapped, or has a [CacheInvalidate] method
            Assert.Empty(TagCoverage.Uninvalidated(provider, alsoInvalidatedBy: mapping.AllTags));
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

namespace Docs.EntityFrameworkCore.SharedRead
{
    public sealed class Recipe
    {
        public int Id { get; set; }
        public Guid HouseholdId { get; set; }
        public string Title { get; set; } = "";
    }

    public record RecipeDto(int Id, string Title);

    /// <summary>The app's context for requests.</summary>
    public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
    {
        public DbSet<Recipe> Recipes => Set<Recipe>();
    }

    /// <summary>Sets the signed-in user's household on each connection, for the row-level security policies.</summary>
    public sealed class SignedInHousehold : DbConnectionInterceptor;

    // begin-snippet: efcore-shared-read-context
    // For cached reads: a context of its own, which nothing sets up from the request
    public sealed class ReadDb(DbContextOptions<ReadDb> options) : DbContext(options)
    {
        public DbSet<Recipe> Recipes => Set<Recipe>();
    }

    public interface IRecipeReads
    {
        [Cache(Duration = "10m", Tags = ["recipes:{householdId}"])]
        Task<IReadOnlyList<RecipeDto>> ListAsync(Guid householdId);
    }

    public sealed class RecipeReads(IDbContextFactory<ReadDb> factory) : IRecipeReads
    {
        public async Task<IReadOnlyList<RecipeDto>> ListAsync(Guid householdId)
        {
            // Its own context and connection for each call, also for a refresh that runs after the request
            await using var db = await factory.CreateDbContextAsync();
            // The policies read the household from the argument, which is part of the key, not from the request
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlAsync($"select set_config('app.household', {householdId.ToString()}, false)");
            return await db.Recipes.Where(r => r.HouseholdId == householdId)
                .Select(r => new RecipeDto(r.Id, r.Title)).ToListAsync();
        }
    }
    // end-snippet

    public static class Setup
    {
        public static void Register(WebApplicationBuilder builder, string connectionString, SignedInHousehold signedIn)
        {
            // begin-snippet: efcore-shared-read-register
            // Requests read and write through the app's context, set up for the signed-in user
            builder.Services.AddDbContext<AppDb>(o => o.UseNpgsql(connectionString).AddInterceptors(signedIn));
            // Cached reads use their own
            builder.Services.AddDbContextFactory<ReadDb>(o => o.UseNpgsql(connectionString));
            builder.Services.AddScoped<IRecipeReads, RecipeReads>();

            // The writes' context is the one connected to MethodCache
            builder.Services.AddMethodCache(mc => mc.UseEntityFrameworkCore<AppDb>(ef => ef
                .Entity<Recipe>("recipes:{HouseholdId}")));
            // end-snippet
        }
    }
}
