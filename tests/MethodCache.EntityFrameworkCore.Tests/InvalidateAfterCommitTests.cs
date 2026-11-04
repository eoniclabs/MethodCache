using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.EntityFrameworkCore.Tests;

public sealed class InvalidateAfterCommitTests
{
    [Fact]
    public async Task Without_a_transaction_the_tags_wait_for_the_next_SaveChanges()
    {
        await using var shop = await Shop.CreateAsync();
        await shop.Reads.GetAsync(42);

        await using var db = await shop.CreateDbAsync();
        db.InvalidateAfterCommit("product:42");
        await shop.Reads.GetAsync(42);
        shop.Counter.Calls.Should().Be(1);

        await db.SaveChangesAsync();
        await shop.Reads.GetAsync(42);
        shop.Counter.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Tags_without_a_later_SaveChanges_are_invalidated_when_the_context_is_disposed()
    {
        await using var shop = await Shop.CreateAsync();
        await shop.Reads.GetAsync(42);

        await using (var db = await shop.CreateDbAsync())
        {
            await db.Products.Where(p => p.Id == 42).ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "Bulk"));
            db.InvalidateAfterCommit("product:42");
        }

        (await shop.Reads.GetAsync(42))!.Name.Should().Be("Bulk");
    }

    [Fact]
    public async Task A_pooled_context_invalidates_its_tags_when_returned_and_starts_its_next_lease_empty()
    {
        await using var shop = await Shop.CreateAsync(Registration.Pool);
        await shop.Reads.GetAsync(42);

        using (var scope = shop.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ShopDb>().InvalidateAfterCommit("product:42");
        }
        await shop.Reads.GetAsync(42);
        shop.Counter.Calls.Should().Be(2);

        using (var scope = shop.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ShopDb>().SaveChangesAsync();
        }
        await shop.Reads.GetAsync(42);
        shop.Counter.Calls.Should().Be(2);
    }

    [Fact]
    public async Task A_context_that_is_not_connected_throws_with_the_registration_to_add()
    {
        await using var shop = await Shop.CreateAsync();
        using var scope = shop.Services.CreateScope();
        var other = scope.ServiceProvider.GetRequiredService<OtherDb>();

        var invalidate = () => other.InvalidateAfterCommit("product:42");

        invalidate.Should().Throw<InvalidOperationException>().WithMessage("*mc.UseEntityFrameworkCore<OtherDb>()*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task An_empty_tag_is_rejected(string? tag)
    {
        await using var shop = await Shop.CreateAsync();
        await using var db = await shop.CreateDbAsync();

        var invalidate = () => db.InvalidateAfterCommit(tag!);

        invalidate.Should().Throw<ArgumentException>();
    }
}
