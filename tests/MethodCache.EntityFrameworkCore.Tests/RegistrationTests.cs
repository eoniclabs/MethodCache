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
}
