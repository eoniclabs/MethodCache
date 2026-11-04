using MethodCache.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MethodCache.EntityFrameworkCore.Tests;

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Sku { get; set; } = "";
    public int CategoryId { get; set; }
    public Category? Category { get; set; }
}

public sealed class DiscountedProduct : Product
{
    public int Percent { get; set; }
}

public sealed class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>A row without a CLR key property: its category is a shadow foreign key.</summary>
public sealed class Review
{
    public int Id { get; set; }
    public int Stars { get; set; }
}

public sealed class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Review> Reviews => Set<Review>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DiscountedProduct>();
        modelBuilder.Entity<Product>().HasIndex(p => p.Sku).IsUnique();
        modelBuilder.Entity<Review>().Property<int>("ProductId");
        modelBuilder.Entity<Review>().HasOne<Product>().WithMany().HasForeignKey("ProductId");
    }
}

/// <summary>A context that isn't connected to MethodCache.</summary>
public sealed class OtherDb(DbContextOptions<OtherDb> options) : DbContext(options);

public record ProductDto(int Id, string Name);

public interface IProductReads
{
    [Cache(Duration = "00:10:00", Tags = ["product:{id}"])]
    Task<ProductDto?> GetAsync(int id);

    [Cache(Duration = "00:10:00", Tags = ["products"])]
    Task<int> CountAsync();

    [Cache(Duration = "00:10:00", Tags = ["category:{categoryId}"])]
    Task<int> CountInCategoryAsync(int categoryId);

    [Cache(Duration = "00:10:00", Tags = ["sku:{sku}"])]
    Task<string?> NameBySkuAsync(string sku);

    [Cache(Duration = "00:10:00", Tags = ["reviews:{productId}"])]
    Task<int> ReviewCountAsync(int productId);
}

/// <summary>Reads through a fresh context and counts how often the cache let a call through.</summary>
public sealed class ProductReads(IDbContextFactory<ShopDb> factory, ReadCounter counter) : IProductReads
{
    public async Task<ProductDto?> GetAsync(int id)
    {
        counter.Increment();
        await using var db = await factory.CreateDbContextAsync();
        return await db.Products.Where(p => p.Id == id).Select(p => new ProductDto(p.Id, p.Name)).SingleOrDefaultAsync();
    }

    public async Task<int> CountAsync()
    {
        counter.Increment();
        await using var db = await factory.CreateDbContextAsync();
        return await db.Products.CountAsync();
    }

    public async Task<int> CountInCategoryAsync(int categoryId)
    {
        counter.Increment();
        await using var db = await factory.CreateDbContextAsync();
        return await db.Products.CountAsync(p => p.CategoryId == categoryId);
    }

    public async Task<string?> NameBySkuAsync(string sku)
    {
        counter.Increment();
        await using var db = await factory.CreateDbContextAsync();
        return await db.Products.Where(p => p.Sku == sku).Select(p => p.Name).SingleOrDefaultAsync();
    }

    public async Task<int> ReviewCountAsync(int productId)
    {
        counter.Increment();
        await using var db = await factory.CreateDbContextAsync();
        return await db.Reviews.CountAsync(r => EF.Property<int>(r, "ProductId") == productId);
    }
}

public sealed class ReadCounter
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public void Increment() => Interlocked.Increment(ref _calls);
}

public enum Registration { Context, Pool, Factory, NonRelational }

/// <summary>A host with an in-memory SQLite shop, a cached read service and MethodCache connected to the context.</summary>
public sealed class Shop : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private Shop(SqliteConnection connection, IHost host)
    {
        _connection = connection;
        Host = host;
    }

    public IHost Host { get; }

    public IServiceProvider Services => Host.Services;

    public IProductReads Reads => Services.GetRequiredService<IProductReads>();

    public ReadCounter Counter => Services.GetRequiredService<ReadCounter>();

    public static readonly Action<EntityFrameworkCoreOptions> DefaultTags = ef => ef
        .Entity<Product>("product:{Id}", "products", "category:{CategoryId}", "sku:{Sku}")
        .Entity<Review>("reviews:{ProductId}");

    public static async Task<Shop> CreateAsync(Registration registration = Registration.Context,
        Action<EntityFrameworkCoreOptions>? tags = null, Action<IServiceCollection>? configure = null, bool start = true)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        var services = builder.Services;
        // MethodCache first: the order against AddDbContext doesn't matter
        services.AddMethodCache(mc => mc.UseEntityFrameworkCore<ShopDb>(tags ?? DefaultTags));
        switch (registration)
        {
            case Registration.Context:
                services.AddDbContext<ShopDb>(o => o.UseSqlite(connection));
                services.AddDbContextFactory<ShopDb>(o => o.UseSqlite(connection));
                break;
            case Registration.Pool:
                services.AddDbContextPool<ShopDb>(o => o.UseSqlite(connection));
                services.AddPooledDbContextFactory<ShopDb>(o => o.UseSqlite(connection));
                break;
            case Registration.Factory:
                services.AddDbContextFactory<ShopDb>(o => o.UseSqlite(connection));
                break;
            case Registration.NonRelational:
                // EF Core's in-memory provider stands in for a document database: no relational transactions
                var database = "shop-" + Guid.NewGuid().ToString("N");
                services.AddDbContextFactory<ShopDb>(o => o.UseInMemoryDatabase(database));
                break;
        }
        services.AddDbContext<OtherDb>(o => o.UseSqlite(connection));
        services.AddSingleton<ReadCounter>();
        services.AddScoped<IProductReads, ProductReads>();
        configure?.Invoke(services);
        // Registered after the cached service, as AddMethodCache's order rule asks
        services.AddMethodCache();

        var host = builder.Build();
        var shop = new Shop(connection, host);
        await using (var db = await shop.CreateDbAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.Categories.AddRange(new Category { Id = 1, Name = "Tools" }, new Category { Id = 2, Name = "Toys" });
            db.Products.Add(new Product { Id = 42, Name = "Hammer", Sku = "ham:mer", CategoryId = 1 });
            await db.SaveChangesAsync();
        }
        if (start) await host.StartAsync();
        return shop;
    }

    /// <summary>A context as the app would get one; disposing it returns a pooled one to its pool.</summary>
    public async Task<ShopDb> CreateDbAsync()
        => await Services.GetRequiredService<IDbContextFactory<ShopDb>>().CreateDbContextAsync();

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
        await _connection.DisposeAsync();
    }
}
