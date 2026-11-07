using MethodCache.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

namespace MethodCache.EntityFrameworkCore.Tests;

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Sku { get; set; } = "";
    public int CategoryId { get; set; }
    public Category? Category { get; set; }
    public Dimensions Size { get; set; } = new();
    public List<Label> Labels { get; set; } = [];
}

/// <summary>Owned by a product, in the product's row.</summary>
public sealed class Dimensions
{
    public int Weight { get; set; }
}

/// <summary>Linked to products many-to-many, through a join table without a class.</summary>
public sealed class Label
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<Product> Products { get; set; } = [];
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

/// <summary>A row with a column the database computes from another one.</summary>
public sealed class Article
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
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
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<Article> Articles => Set<Article>();

    /// <summary>Hides every product from queries, as a tenant filter hides another tenant's rows.</summary>
    public bool HideProducts { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DiscountedProduct>();
        modelBuilder.Entity<Product>().HasQueryFilter(p => !HideProducts);
        modelBuilder.Entity<Product>().HasIndex(p => p.Sku).IsUnique();
        modelBuilder.Entity<Product>().OwnsOne(p => p.Size);
        modelBuilder.Entity<Product>().HasMany(p => p.Labels).WithMany(l => l.Products);
        modelBuilder.Entity<Review>().Property<int>("ProductId");
        modelBuilder.Entity<Review>().HasOne<Product>().WithMany().HasForeignKey("ProductId");
        if (Database.IsRelational()) modelBuilder.Entity<Article>().Property(a => a.Slug).HasComputedColumnSql("upper(Title)", stored: true);
    }
}

/// <summary>A context that isn't connected to MethodCache.</summary>
public sealed class OtherDb(DbContextOptions<OtherDb> options) : DbContext(options);

public record ProductDto(int Id, string Name);

public interface IProductReads
{
    [Cache(Duration = "10m", Tags = ["product:{id}"])]
    Task<ProductDto?> GetAsync(int id);

    [Cache(Duration = "10m", Tags = ["products"])]
    Task<int> CountAsync();

    [Cache(Duration = "10m", Tags = ["category:{categoryId}"])]
    Task<int> CountInCategoryAsync(int categoryId);

    [Cache(Duration = "10m", Tags = ["sku:{sku}"])]
    Task<string?> NameBySkuAsync(string sku);

    [Cache(Duration = "10m", Tags = ["reviews:{productId}"])]
    Task<int> ReviewCountAsync(int productId);

    [Cache(Duration = "10m", Tags = ["slug:{slug}"])]
    Task<int?> ArticleBySlugAsync(string slug);
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

    public async Task<int?> ArticleBySlugAsync(string slug)
    {
        counter.Increment();
        await using var db = await factory.CreateDbContextAsync();
        return await db.Articles.Where(a => a.Slug == slug).Select(a => (int?)a.Id).SingleOrDefaultAsync();
    }
}

public sealed class ReadCounter
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public void Increment() => Interlocked.Increment(ref _calls);
}

public enum Registration { Context, Pool, Factory, NonRelational, File }

/// <summary>A host with an in-memory SQLite shop, a cached read service and MethodCache connected to the context.</summary>
public sealed class Shop : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string? _file;

    private Shop(SqliteConnection connection, string? file, IHost host)
    {
        _connection = connection;
        _file = file;
        Host = host;
    }

    /// <summary>With <see cref="Registration.File"/>: each context opens its own connection to this database.</summary>
    public string ConnectionString => _connection.ConnectionString;

    public IHost Host { get; }

    public IServiceProvider Services => Host.Services;

    public IProductReads Reads => Services.GetRequiredService<IProductReads>();

    public ReadCounter Counter => Services.GetRequiredService<ReadCounter>();

    public static readonly Action<EntityFrameworkCoreOptions> DefaultTags = ef => ef
        .Entity<Product>("product:{Id}", "products", "category:{CategoryId}", "sku:{Sku}")
        .Entity<Review>("reviews:{ProductId}")
        .Entity<Article>("slug:{Slug}");

    public static async Task<Shop> CreateAsync(Registration registration = Registration.Context,
        Action<EntityFrameworkCoreOptions>? tags = null, Action<IServiceCollection>? configure = null, bool start = true)
    {
        // A file in WAL mode lets a reader on another connection see only committed rows, as with a server
        var file = registration == Registration.File ? Path.Combine(Path.GetTempPath(), "methodcache-ef-" + Guid.NewGuid().ToString("N") + ".db") : null;
        var connection = new SqliteConnection(file == null ? "DataSource=:memory:" : $"Data Source={file};Pooling=False");
        await connection.OpenAsync();
        if (file != null)
        {
            await using var wal = connection.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            await wal.ExecuteNonQueryAsync();
        }

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        // On Windows the default host logs warnings to the Event Log, which a build agent signed in over SSH may not
        // write to. A failed write throws out of EF Core's error logging, in place of the exception SaveChanges
        // reports and before EF Core calls the interceptors.
        if (OperatingSystem.IsWindows()) builder.Logging.AddFilter<EventLogLoggerProvider>(static _ => false);
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
            case Registration.File:
                services.AddDbContextFactory<ShopDb>(o => o.UseSqlite(connection.ConnectionString));
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
        var shop = new Shop(connection, file, host);
        await using (var db = await shop.CreateDbAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.Categories.AddRange(new Category { Id = 1, Name = "Tools" }, new Category { Id = 2, Name = "Toys" });
            db.Products.Add(new Product { Id = 42, Name = "Hammer", Sku = "ham:mer", CategoryId = 1, Size = new Dimensions { Weight = 600 } });
            db.Labels.Add(new Label { Id = 1, Name = "Sale" });
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
        if (_file != null)
        {
            foreach (var path in new[] { _file, _file + "-wal", _file + "-shm" }) File.Delete(path);
        }
    }
}
