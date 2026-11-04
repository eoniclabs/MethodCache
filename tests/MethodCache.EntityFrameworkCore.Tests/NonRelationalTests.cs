using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace MethodCache.EntityFrameworkCore.Tests;

/// <summary>
/// Document databases (Cosmos DB, MongoDB) use EF Core without its relational part. SaveChanges is the commit there,
/// and the tags are invalidated after it like with any other provider.
/// </summary>
public sealed class NonRelationalTests
{
    [Fact]
    public async Task SaveChanges_invalidates_with_a_provider_that_is_not_relational()
    {
        await using var shop = await Shop.CreateAsync(Registration.NonRelational);
        await shop.Reads.GetAsync(42);
        await shop.Reads.CountAsync();

        await using (var db = await shop.CreateDbAsync())
        {
            (await db.Products.SingleAsync(p => p.Id == 42)).Name = "Document";
            db.Products.Add(new Product { Name = "Saw", Sku = "saw", CategoryId = 1 });
            await db.SaveChangesAsync();
        }

        (await shop.Reads.GetAsync(42))!.Name.Should().Be("Document");
        (await shop.Reads.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task InvalidateAfterCommit_works_with_a_provider_that_is_not_relational()
    {
        await using var shop = await Shop.CreateAsync(Registration.NonRelational);
        await shop.Reads.GetAsync(42);

        await using (var db = await shop.CreateDbAsync())
        {
            db.InvalidateAfterCommit("product:42");
            db.SaveChanges();
        }

        await shop.Reads.GetAsync(42);
        shop.Counter.Calls.Should().Be(2);
    }
}
