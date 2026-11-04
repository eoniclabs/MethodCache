using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.EntityFrameworkCore.Tests;

public sealed class SaveChangesTests
{
    [Theory]
    [InlineData(Registration.Context)]
    [InlineData(Registration.Pool)]
    [InlineData(Registration.Factory)]
    public async Task A_changed_entity_invalidates_its_tags_after_SaveChanges(Registration registration)
    {
        await using var shop = await Shop.CreateAsync(registration);
        (await shop.Reads.GetAsync(42))!.Name.Should().Be("Hammer");
        await shop.Reads.GetAsync(42);
        shop.Counter.Calls.Should().Be(1);

        await using (var db = await shop.CreateDbAsync())
        {
            var product = await db.Products.SingleAsync(p => p.Id == 42);
            product.Name = "Sledgehammer";
            await db.SaveChangesAsync();
        }

        (await shop.Reads.GetAsync(42))!.Name.Should().Be("Sledgehammer");
        shop.Counter.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Synchronous_SaveChanges_invalidates_before_it_returns()
    {
        await using var shop = await Shop.CreateAsync();
        await shop.Reads.GetAsync(42);

        using (var db = await shop.CreateDbAsync())
        {
            db.Products.Single(p => p.Id == 42).Name = "Mallet";
            db.SaveChanges();
        }

        (await shop.Reads.GetAsync(42))!.Name.Should().Be("Mallet");
    }

    [Fact]
    public async Task An_added_entity_uses_its_generated_key()
    {
        await using var shop = await Shop.CreateAsync();
        // The id the database will generate next, cached as missing before the row exists
        (await shop.Reads.GetAsync(43)).Should().BeNull();
        await shop.Reads.CountAsync();

        await using (var db = await shop.CreateDbAsync())
        {
            db.Products.Add(new Product { Name = "Saw", Sku = "saw", CategoryId = 1 });
            await db.SaveChangesAsync();
        }

        (await shop.Reads.GetAsync(43))!.Name.Should().Be("Saw");
        (await shop.Reads.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_deleted_entity_invalidates_its_tags()
    {
        await using var shop = await Shop.CreateAsync();
        await shop.Reads.GetAsync(42);

        await using (var db = await shop.CreateDbAsync())
        {
            db.Products.Remove(await db.Products.SingleAsync(p => p.Id == 42));
            await db.SaveChangesAsync();
        }

        (await shop.Reads.GetAsync(42)).Should().BeNull();
    }

    [Fact]
    public async Task A_changed_foreign_key_invalidates_the_old_and_the_new_value()
    {
        await using var shop = await Shop.CreateAsync();
        (await shop.Reads.CountInCategoryAsync(1)).Should().Be(1);
        (await shop.Reads.CountInCategoryAsync(2)).Should().Be(0);

        await using (var db = await shop.CreateDbAsync())
        {
            (await db.Products.SingleAsync(p => p.Id == 42)).CategoryId = 2;
            await db.SaveChangesAsync();
        }

        (await shop.Reads.CountInCategoryAsync(1)).Should().Be(0);
        (await shop.Reads.CountInCategoryAsync(2)).Should().Be(1);
    }

    [Fact]
    public async Task A_shadow_foreign_key_fills_in_a_placeholder()
    {
        await using var shop = await Shop.CreateAsync();
        (await shop.Reads.ReviewCountAsync(42)).Should().Be(0);

        await using (var db = await shop.CreateDbAsync())
        {
            var review = new Review { Stars = 5 };
            db.Reviews.Add(review);
            db.Entry(review).Property("ProductId").CurrentValue = 42;
            await db.SaveChangesAsync();
        }

        (await shop.Reads.ReviewCountAsync(42)).Should().Be(1);
    }

    [Fact]
    public async Task Values_are_formatted_as_cached_methods_format_arguments()
    {
        // The SKU holds a colon, which a tag escapes: "sku:ham\:mer" on both sides
        await using var shop = await Shop.CreateAsync();
        (await shop.Reads.NameBySkuAsync("ham:mer")).Should().Be("Hammer");

        await using (var db = await shop.CreateDbAsync())
        {
            (await db.Products.SingleAsync(p => p.Id == 42)).Name = "Claw hammer";
            await db.SaveChangesAsync();
        }

        (await shop.Reads.NameBySkuAsync("ham:mer")).Should().Be("Claw hammer");
    }

    [Fact]
    public async Task A_derived_entity_invalidates_the_tags_of_its_base_type()
    {
        await using var shop = await Shop.CreateAsync();
        await shop.Reads.CountAsync();

        await using (var db = await shop.CreateDbAsync())
        {
            db.Products.Add(new DiscountedProduct { Name = "Old saw", Sku = "old-saw", CategoryId = 1, Percent = 20 });
            await db.SaveChangesAsync();
        }

        (await shop.Reads.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_failed_SaveChanges_invalidates_nothing()
    {
        await using var shop = await Shop.CreateAsync();
        await shop.Reads.CountAsync();

        await using (var db = await shop.CreateDbAsync())
        {
            // The SKU is unique, so the insert fails
            db.Products.Add(new Product { Name = "Copy", Sku = "ham:mer", CategoryId = 1 });
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<DbUpdateException>();
        }

        await shop.Reads.CountAsync();
        shop.Counter.Calls.Should().Be(1);
    }

    [Fact]
    public async Task An_unmapped_entity_invalidates_nothing()
    {
        await using var shop = await Shop.CreateAsync(tags: ef => ef.Entity<Review>("reviews:{ProductId}"));
        await shop.Reads.GetAsync(42);

        await using (var db = await shop.CreateDbAsync())
        {
            (await db.Products.SingleAsync(p => p.Id == 42)).Name = "Unmapped";
            await db.SaveChangesAsync();
        }

        await shop.Reads.GetAsync(42);
        shop.Counter.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_context_registered_through_AddDbContext_is_connected()
    {
        await using var shop = await Shop.CreateAsync();
        await shop.Reads.GetAsync(42);

        using (var scope = shop.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
            (await db.Products.SingleAsync(p => p.Id == 42)).Name = "Scoped";
            await db.SaveChangesAsync();
        }

        (await shop.Reads.GetAsync(42))!.Name.Should().Be("Scoped");
    }
}
