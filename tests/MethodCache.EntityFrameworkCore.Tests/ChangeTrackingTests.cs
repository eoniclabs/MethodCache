using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.EntityFrameworkCore.Tests;

/// <summary>Rows that change in ways other than load, change and save.</summary>
public sealed class ChangeTrackingTests
{
    [Fact]
    public async Task A_change_to_an_owned_type_invalidates_the_owner()
    {
        await using var shop = await Shop.CreateAsync();
        await shop.Reads.NameBySkuAsync("ham:mer");

        await using (var db = await shop.CreateDbAsync())
        {
            // Only the owned row changes: the product's own row is unchanged
            (await db.Products.SingleAsync(p => p.Id == 42)).Size.Weight = 750;
            await db.SaveChangesAsync();
        }

        await shop.Reads.NameBySkuAsync("ham:mer");
        shop.Counter.Calls.Should().Be(2);
    }

    [Fact]
    public async Task A_many_to_many_link_invalidates_the_mapped_side()
    {
        await using var shop = await Shop.CreateAsync();
        await shop.Reads.GetAsync(42);

        await using (var db = await shop.CreateDbAsync())
        {
            var product = await db.Products.Include(p => p.Labels).SingleAsync(p => p.Id == 42);
            product.Labels.Add(await db.Labels.SingleAsync(l => l.Id == 1));
            await db.SaveChangesAsync();
        }
        await shop.Reads.GetAsync(42);
        shop.Counter.Calls.Should().Be(2);

        await using (var db = await shop.CreateDbAsync())
        {
            var product = await db.Products.Include(p => p.Labels).SingleAsync(p => p.Id == 42);
            product.Labels.Clear();
            await db.SaveChangesAsync();
        }
        await shop.Reads.GetAsync(42);
        shop.Counter.Calls.Should().Be(3);
    }

    [Fact]
    public async Task A_cascade_delete_deferred_to_SaveChanges_invalidates_the_deleted_rows()
    {
        await using var shop = await Shop.CreateAsync();
        (await shop.Reads.CountAsync()).Should().Be(1);

        await using (var db = await shop.CreateDbAsync())
        {
            db.ChangeTracker.CascadeDeleteTiming = CascadeTiming.OnSaveChanges;
            await db.Products.SingleAsync(p => p.Id == 42);
            // The product is still unchanged here: EF Core deletes it during the save
            db.Categories.Remove(await db.Categories.SingleAsync(c => c.Id == 1));
            await db.SaveChangesAsync();
        }

        (await shop.Reads.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_updated_row_that_was_not_loaded_invalidates_the_tags_of_its_stored_values()
    {
        await using var shop = await Shop.CreateAsync();
        (await shop.Reads.CountInCategoryAsync(1)).Should().Be(1);
        (await shop.Reads.CountInCategoryAsync(2)).Should().Be(0);

        await using (var db = await shop.CreateDbAsync())
        {
            // Update takes the new values as the original ones: the stored category comes from the database
            db.Products.Update(new Product { Id = 42, Name = "Moved", Sku = "ham:mer", CategoryId = 2, Size = new Dimensions { Weight = 600 } });
            await db.SaveChangesAsync();
        }

        (await shop.Reads.CountInCategoryAsync(1)).Should().Be(0);
        (await shop.Reads.CountInCategoryAsync(2)).Should().Be(1);
    }

    [Fact]
    public async Task A_row_a_query_filter_hides_is_read_for_its_stored_values_too()
    {
        await using var shop = await Shop.CreateAsync();
        (await shop.Reads.CountInCategoryAsync(1)).Should().Be(1);

        await using (var db = await shop.CreateDbAsync())
        {
            // A filter, such as one for the current tenant, hides rows from queries but not from the stored values
            db.HideProducts = true;
            (await db.Products.AnyAsync()).Should().BeFalse();
            db.Products.Update(new Product { Id = 42, Name = "Moved", Sku = "ham:mer", CategoryId = 2, Size = new Dimensions { Weight = 600 } });
            await db.SaveChangesAsync();
        }

        (await shop.Reads.CountInCategoryAsync(1)).Should().Be(0);
    }

    [Fact]
    public async Task A_row_deleted_by_its_key_alone_invalidates_the_tags_of_its_stored_values()
    {
        await using var shop = await Shop.CreateAsync();
        (await shop.Reads.NameBySkuAsync("ham:mer")).Should().Be("Hammer");
        (await shop.Reads.CountInCategoryAsync(1)).Should().Be(1);

        await using (var db = await shop.CreateDbAsync())
        {
            db.Products.Remove(new Product { Id = 42 });
            await db.SaveChangesAsync();
        }

        (await shop.Reads.NameBySkuAsync("ham:mer")).Should().BeNull();
        (await shop.Reads.CountInCategoryAsync(1)).Should().Be(0);
    }

    [Fact]
    public async Task A_synchronous_save_reads_the_stored_values_too()
    {
        await using var shop = await Shop.CreateAsync();
        (await shop.Reads.CountInCategoryAsync(1)).Should().Be(1);

        using (var db = await shop.CreateDbAsync())
        {
            db.Products.Remove(new Product { Id = 42 });
            db.SaveChanges();
        }

        (await shop.Reads.CountInCategoryAsync(1)).Should().Be(0);
    }

    [Fact]
    public async Task A_change_to_an_owned_type_of_an_attached_row_invalidates_the_owner_as_stored()
    {
        await using var shop = await Shop.CreateAsync();
        (await shop.Reads.NameBySkuAsync("ham:mer")).Should().Be("Hammer");

        await using (var db = await shop.CreateDbAsync())
        {
            var product = new Product { Id = 42, Size = new Dimensions { Weight = 600 } };
            db.Attach(product);
            product.Size.Weight = 900;
            await db.SaveChangesAsync();
        }

        await shop.Reads.NameBySkuAsync("ham:mer");
        shop.Counter.Calls.Should().Be(2);
    }

    [Fact]
    public async Task An_attached_row_with_one_changed_property_invalidates_the_tags_of_the_rest_as_stored()
    {
        await using var shop = await Shop.CreateAsync();
        (await shop.Reads.NameBySkuAsync("ham:mer")).Should().Be("Hammer");

        await using (var db = await shop.CreateDbAsync())
        {
            // The stub's SKU is empty and isn't written: the row keeps "ham:mer"
            var product = new Product { Id = 42 };
            db.Attach(product);
            product.Name = "Renamed";
            await db.SaveChangesAsync();
        }

        (await shop.Reads.NameBySkuAsync("ham:mer")).Should().Be("Renamed");
    }

    [Fact]
    public async Task A_loaded_row_is_not_read_again_when_it_is_saved()
    {
        var commands = new CommandLog();
        await using var shop = await Shop.CreateAsync(configure: services =>
            services.ConfigureDbContext<ShopDb>(o => o.AddInterceptors(commands)));
        await using var db = await shop.CreateDbAsync();
        var product = await db.Products.SingleAsync(p => p.Id == 42);
        product.CategoryId = 2;
        commands.Texts.Clear();

        await db.SaveChangesAsync();

        commands.Texts.Should().NotContain(text => text.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Changes_made_by_a_later_SavingChanges_interceptor_are_invalidated()
    {
        // Registered after MethodCache, so it runs after MethodCache's own SavingChanges
        var audit = new AuditInterceptor();
        await using var shop = await Shop.CreateAsync(configure: services =>
            services.ConfigureDbContext<ShopDb>(o => o.AddInterceptors(audit)));
        audit.Enabled = true;
        (await shop.Reads.GetAsync(42))!.Name.Should().Be("Hammer");
        (await shop.Reads.CountAsync()).Should().Be(1);

        await using (var db = await shop.CreateDbAsync())
        {
            await db.Products.SingleAsync(p => p.Id == 42);
            db.Categories.Add(new Category { Id = 3, Name = "Garden" });
            await db.SaveChangesAsync();
        }

        (await shop.Reads.GetAsync(42))!.Name.Should().Be("Audited");
        (await shop.Reads.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task SaveChanges_detects_changes_once()
    {
        await using var shop = await Shop.CreateAsync();
        await using var db = await shop.CreateDbAsync();
        (await db.Products.SingleAsync(p => p.Id == 42)).Name = "Once";
        var detections = 0;
        db.ChangeTracker.DetectedAllChanges += (_, _) => detections++;

        await db.SaveChangesAsync();

        detections.Should().Be(1);
    }

    /// <summary>Renames product 42 and adds a product in every save, as an audit or soft-delete interceptor would.</summary>
    private sealed class AuditInterceptor : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Enabled) return ValueTask.FromResult(result);
            var db = (ShopDb)eventData.Context!;
            if (db.Products.Local.FirstOrDefault(p => p.Id == 42) is { } product) product.Name = "Audited";
            db.Products.Add(new Product { Name = "Audit", Sku = "audit-" + Guid.NewGuid().ToString("N"), CategoryId = 1 });
            return ValueTask.FromResult(result);
        }
    }
}
