using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MethodCache.EntityFrameworkCore.Tests;

/// <summary>Tags mapped with <c>WhenDeleted</c> are invalidated when a row is deleted, and on no other change.</summary>
public sealed class WhenDeletedTests
{
    [Fact]
    public async Task A_change_does_not_invalidate_the_tags_for_a_deleted_row()
    {
        await using var shop = await Shop.CreateAsync(tags: ef => ef.Entity<Category>(c => c.WhenDeleted("products")));
        (await shop.Reads.CountAsync()).Should().Be(1);

        await using (var db = await shop.CreateDbAsync())
        {
            (await db.Categories.SingleAsync(c => c.Id == 1)).Name = "Renamed";
            await db.SaveChangesAsync();
        }

        await shop.Reads.CountAsync();
        shop.Counter.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Deleting_a_row_invalidates_what_the_database_deletes_with_it()
    {
        await using var shop = await Shop.CreateAsync(tags: ef => ef.Entity<Category>(c => c.WhenDeleted("products")));
        (await shop.Reads.CountAsync()).Should().Be(1);

        await using (var db = await shop.CreateDbAsync())
        {
            // Product 42 isn't loaded: the database deletes it through its foreign key's ON DELETE CASCADE
            db.Categories.Remove(await db.Categories.SingleAsync(c => c.Id == 1));
            await db.SaveChangesAsync();
        }

        (await shop.Reads.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_row_deleted_without_loading_it_fills_the_tags_from_the_stored_row()
    {
        await using var shop = await Shop.CreateAsync(tags: ef => ef.Entity<Product>(p => p.WhenDeleted("sku:{Sku}")));
        (await shop.Reads.NameBySkuAsync("ham:mer")).Should().Be("Hammer");

        await using (var db = await shop.CreateDbAsync())
        {
            db.Products.Remove(new Product { Id = 42 });
            await db.SaveChangesAsync();
        }

        (await shop.Reads.NameBySkuAsync("ham:mer")).Should().BeNull();
    }

    [Fact]
    public async Task A_changed_row_that_the_save_deletes_by_cascade_invalidates_its_tags_for_a_deleted_row()
    {
        await using var shop = await Shop.CreateAsync(tags: ef => ef.Entity<Product>(p => p.WhenDeleted("sku:{Sku}")));
        (await shop.Reads.NameBySkuAsync("ham:mer")).Should().Be("Hammer");

        await using (var db = await shop.CreateDbAsync())
        {
            db.ChangeTracker.CascadeDeleteTiming = CascadeTiming.OnSaveChanges;
            // Changed first, then deleted by the save itself when it cascades the category's deletion
            (await db.Products.SingleAsync(p => p.Id == 42)).Name = "Renamed";
            db.Categories.Remove(await db.Categories.SingleAsync(c => c.Id == 1));
            await db.SaveChangesAsync();
        }

        (await shop.Reads.NameBySkuAsync("ham:mer")).Should().BeNull();
    }

    [Fact]
    public async Task Tags_and_tags_for_a_deleted_row_combine()
    {
        await using var shop = await Shop.CreateAsync(tags: ef => ef.Entity<Product>(p => p.Tags("product:{Id}").WhenDeleted("products")));
        await shop.Reads.GetAsync(42);
        (await shop.Reads.CountAsync()).Should().Be(1);

        await using (var db = await shop.CreateDbAsync())
        {
            (await db.Products.SingleAsync(p => p.Id == 42)).Name = "Renamed";
            await db.SaveChangesAsync();
        }

        (await shop.Reads.GetAsync(42))!.Name.Should().Be("Renamed");
        await shop.Reads.CountAsync();
        shop.Counter.Calls.Should().Be(3);

        await using (var db = await shop.CreateDbAsync())
        {
            db.Products.Remove(await db.Products.SingleAsync(p => p.Id == 42));
            await db.SaveChangesAsync();
        }

        (await shop.Reads.CountAsync()).Should().Be(0);
        (await shop.Reads.GetAsync(42)).Should().BeNull();
    }

    [Fact]
    public async Task A_derived_type_deletes_with_the_tags_of_its_base_type()
    {
        await using var shop = await Shop.CreateAsync(tags: ef => ef.Entity<Product>(p => p.WhenDeleted("products")));

        await using (var db = await shop.CreateDbAsync())
        {
            db.Products.Add(new DiscountedProduct { Id = 7, Name = "Cheap", Sku = "cheap", CategoryId = 2, Percent = 10 });
            await db.SaveChangesAsync();
        }
        (await shop.Reads.CountAsync()).Should().Be(2);

        await using (var db = await shop.CreateDbAsync())
        {
            db.Products.Remove(await db.Products.SingleAsync(p => p.Id == 7));
            await db.SaveChangesAsync();
        }

        (await shop.Reads.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_placeholder_that_is_not_a_property_stops_the_host()
    {
        var start = () => Shop.CreateAsync(tags: ef => ef.Entity<Category>(c => c.WhenDeleted("category:{CategoryId}")));

        (await start.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*\"category:{CategoryId}\" on Category: {CategoryId} is not a property*");
    }

    [Fact]
    public void A_mapping_without_tags_is_refused()
    {
        var options = new EntityFrameworkCoreOptions();

        var map = () => options.Entity<Category>(_ => { });

        map.Should().Throw<ArgumentException>().WithMessage("*Tags(...) or WhenDeleted(...)*");
        options.Entities.Should().BeEmpty();
    }
}
