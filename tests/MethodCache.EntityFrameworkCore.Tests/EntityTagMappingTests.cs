using FluentAssertions;
using MethodCache.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.EntityFrameworkCore.Tests;

/// <summary>The mapping can be read, and MethodCache.Testing sees the tags a save invalidates.</summary>
public sealed class EntityTagMappingTests
{
    [Fact]
    public async Task The_mapping_lists_each_entity_type_and_its_tags()
    {
        await using var shop = await Shop.CreateAsync(tags: ef => ef
            .Entity<Product>("product:{Id}", "products")
            .Entity<Category>(c => c.Tags("category:{Id}").WhenDeleted("products")));

        var mapping = shop.Services.GetRequiredService<EntityTagMapping<ShopDb>>();

        mapping.Entities.Should().HaveCount(2);
        mapping.Entities[0].EntityType.Should().Be(typeof(Product));
        mapping.Entities[0].Tags.Should().Equal("product:{Id}", "products");
        mapping.Entities[0].WhenDeleted.Should().BeEmpty();
        mapping.Entities[1].EntityType.Should().Be(typeof(Category));
        mapping.Entities[1].Tags.Should().Equal("category:{Id}");
        mapping.Entities[1].WhenDeleted.Should().Equal("products");
        mapping.AllTags.Should().Equal("product:{Id}", "products", "category:{Id}");
    }

    [Fact]
    public async Task The_recorder_lists_the_tags_a_save_invalidates()
    {
        await using var shop = await Shop.CreateAsync(configure: services => services.AddMethodCacheTesting());
        var recorder = shop.Services.GetRequiredService<CacheRecorder>();
        await shop.Reads.GetAsync(42);
        await shop.Reads.GetAsync(42);
        recorder.Loads<IProductReads>(r => r.GetAsync(42)).Should().Be(1);

        await using (var db = await shop.CreateDbAsync())
        {
            (await db.Products.SingleAsync(p => p.Id == 42)).Name = "Mallet";
            await db.SaveChangesAsync();
        }

        recorder.InvalidatedTags.Should().Contain(["product:42", "products", "category:1"]);
        (await shop.Reads.GetAsync(42))!.Name.Should().Be("Mallet");
        recorder.Loads<IProductReads>(r => r.GetAsync(42)).Should().Be(2);
    }

    [Fact]
    public async Task The_mapping_covers_the_tags_of_the_cached_reads()
    {
        await using var shop = await Shop.CreateAsync();
        var mapping = shop.Services.GetRequiredService<EntityTagMapping<ShopDb>>();

        TagCoverage.Uninvalidated(shop.Services, alsoInvalidatedBy: mapping.AllTags).Should().BeEmpty();
        TagCoverage.Uninvalidated(shop.Services).Select(t => t.Tag).Should().Contain("product:{id}");
    }
}
