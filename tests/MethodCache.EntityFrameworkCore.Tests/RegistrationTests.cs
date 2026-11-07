using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MethodCache.EntityFrameworkCore.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public async Task A_placeholder_that_is_not_a_property_stops_the_host()
    {
        var start = () => Shop.CreateAsync(tags: ef => ef.Entity<Product>("product:{ProductId}"));

        (await start.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*\"product:{ProductId}\" on Product: {ProductId} is not a property*Id, *");
    }

    [Fact]
    public async Task A_type_that_is_not_an_entity_stops_the_host()
    {
        var start = () => Shop.CreateAsync(tags: ef => ef.Entity<ProductDto>("product:{Id}"));

        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*ProductDto is not an entity type of ShopDb*");
    }

    [Fact]
    public async Task A_placeholder_matches_the_property_name_in_any_case()
    {
        await using var shop = await Shop.CreateAsync(tags: ef => ef.Entity<Product>("product:{id}"));
        await shop.Reads.GetAsync(42);

        await using (var db = await shop.CreateDbAsync())
        {
            (await db.Products.SingleAsync(p => p.Id == 42)).Name = "Lowercase";
            await db.SaveChangesAsync();
        }

        (await shop.Reads.GetAsync(42))!.Name.Should().Be("Lowercase");
    }

    [Fact]
    public async Task Calling_UseEntityFrameworkCore_twice_adds_to_the_same_mapping()
    {
        await using var shop = await Shop.CreateAsync(tags: ef => ef.Entity<Product>("product:{Id}"),
            configure: services => services.AddMethodCache(mc => mc.UseEntityFrameworkCore<ShopDb>(ef => ef.Entity<Product>("products"))));
        await shop.Reads.GetAsync(42);
        await shop.Reads.CountAsync();

        await using (var db = await shop.CreateDbAsync())
        {
            (await db.Products.SingleAsync(p => p.Id == 42)).Name = "Twice";
            await db.SaveChangesAsync();
        }

        await shop.Reads.GetAsync(42);
        await shop.Reads.CountAsync();
        shop.Counter.Calls.Should().Be(4);
    }

    [Fact]
    public async Task A_container_without_a_host_or_logging_saves_through_the_interceptor()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ShopDb>(o => o.UseSqlite(connection));
        services.AddMethodCache(mc => mc.UseEntityFrameworkCore<ShopDb>(Shop.DefaultTags));
        await using var provider = services.BuildServiceProvider();

        await using var db = await provider.GetRequiredService<IDbContextFactory<ShopDb>>().CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        db.Categories.Add(new Category { Id = 1, Name = "Tools" });
        db.Products.Add(new Product { Id = 42, Name = "Hammer", Sku = "ham:mer", CategoryId = 1 });

        (await db.SaveChangesAsync()).Should().Be(2);
    }

    [Fact]
    public void Entity_needs_at_least_one_tag()
    {
        var services = new ServiceCollection();

        var register = () => services.AddMethodCache(mc => mc.UseEntityFrameworkCore<ShopDb>(ef => ef.Entity<Product>()));

        register.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task A_context_registered_before_MethodCache_is_connected_too()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDbContext<ShopDb>(o => o.UseSqlite(connection));
        builder.Services.AddMethodCache(mc => mc.UseEntityFrameworkCore<ShopDb>(Shop.DefaultTags));
        using var host = builder.Build();
        await host.StartAsync();

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
        db.InvalidateAfterCommit("products");
        await host.StopAsync();
    }

    [Fact]
    public async Task A_context_that_needs_a_request_to_configure_itself_doesnt_stop_the_host()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddScoped<Tenant>();
        builder.Services.AddDbContext<TenantDb>();
        builder.Services.AddMethodCache(mc => mc.UseEntityFrameworkCore<TenantDb>(ef => ef.Entity<Product>("product:{Id}")));
        using var host = builder.Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    public sealed class Tenant
    {
        public string? ConnectionString { get; set; }
    }

    /// <summary>Picks its database from the request's tenant, which doesn't exist when the host starts.</summary>
    public sealed class TenantDb(DbContextOptions<TenantDb> options, Tenant tenant) : DbContext(options)
    {
        public DbSet<Product> Products => Set<Product>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseSqlite(tenant.ConnectionString ?? throw new InvalidOperationException("No tenant."));

        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Product>().Ignore(p => p.Labels).Ignore(p => p.Category);
    }
}
