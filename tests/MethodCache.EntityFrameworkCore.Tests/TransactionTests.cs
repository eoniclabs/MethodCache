using System.Transactions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace MethodCache.EntityFrameworkCore.Tests;

public sealed class TransactionTests
{
    [Fact]
    public async Task Inside_a_transaction_the_tags_wait_for_the_commit()
    {
        await using var shop = await Shop.CreateAsync();
        await shop.Reads.GetAsync(42);

        await using var db = await shop.CreateDbAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            (await db.Products.SingleAsync(p => p.Id == 42)).Name = "Uncommitted";
            await db.SaveChangesAsync();

            // Saved but not committed: the cached value stays, so no reader can cache the old row again after an
            // invalidation that came too early
            (await shop.Reads.GetAsync(42))!.Name.Should().Be("Hammer");
            shop.Counter.Calls.Should().Be(1);

            await transaction.CommitAsync();
        }

        (await shop.Reads.GetAsync(42))!.Name.Should().Be("Uncommitted");
        shop.Counter.Calls.Should().Be(2);
    }

    [Fact]
    public async Task A_synchronous_commit_invalidates()
    {
        await using var shop = await Shop.CreateAsync();
        await shop.Reads.GetAsync(42);

        using (var db = await shop.CreateDbAsync())
        using (var transaction = db.Database.BeginTransaction())
        {
            db.Products.Single(p => p.Id == 42).Name = "Sync";
            db.SaveChanges();
            transaction.Commit();
        }

        (await shop.Reads.GetAsync(42))!.Name.Should().Be("Sync");
    }

    [Fact]
    public async Task A_rollback_drops_the_tags()
    {
        await using var shop = await Shop.CreateAsync();
        await shop.Reads.GetAsync(42);

        await using (var db = await shop.CreateDbAsync())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            (await db.Products.SingleAsync(p => p.Id == 42)).Name = "Rolled back";
            await db.SaveChangesAsync();
            db.InvalidateAfterCommit("products");
            await transaction.RollbackAsync();
        }

        (await shop.Reads.GetAsync(42))!.Name.Should().Be("Hammer");
        shop.Counter.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_rollback_keeps_tags_that_an_earlier_commit_already_invalidated()
    {
        await using var shop = await Shop.CreateAsync();
        await shop.Reads.GetAsync(42);

        await using var db = await shop.CreateDbAsync();
        await using (var first = await db.Database.BeginTransactionAsync())
        {
            (await db.Products.SingleAsync(p => p.Id == 42)).Name = "First";
            await db.SaveChangesAsync();
            await first.CommitAsync();
        }
        (await shop.Reads.GetAsync(42))!.Name.Should().Be("First");

        await using (var second = await db.Database.BeginTransactionAsync())
        {
            db.Products.Add(new Product { Name = "Second", Sku = "second", CategoryId = 1 });
            await db.SaveChangesAsync();
            await second.RollbackAsync();
        }

        await shop.Reads.GetAsync(42);
        shop.Counter.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Several_saves_in_one_transaction_are_invalidated_together_at_the_commit()
    {
        await using var shop = await Shop.CreateAsync();
        await shop.Reads.GetAsync(42);
        await shop.Reads.CountAsync();

        await using (var db = await shop.CreateDbAsync())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            (await db.Products.SingleAsync(p => p.Id == 42)).Name = "One";
            await db.SaveChangesAsync();
            db.Products.Add(new Product { Name = "Two", Sku = "two", CategoryId = 2 });
            await db.SaveChangesAsync();
            await shop.Reads.CountAsync();
            shop.Counter.Calls.Should().Be(2);
            await transaction.CommitAsync();
        }

        (await shop.Reads.GetAsync(42))!.Name.Should().Be("One");
        (await shop.Reads.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_transaction_disposed_without_a_commit_invalidates_its_tags_late()
    {
        // Disposing rolls back without telling EF Core's interceptors. An extra invalidation is harmless; a missing
        // one could keep old data if the transaction had been committed some other way.
        await using var shop = await Shop.CreateAsync();
        await shop.Reads.GetAsync(42);

        await using (var db = await shop.CreateDbAsync())
        {
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                db.InvalidateAfterCommit("product:42");
            }
            await shop.Reads.GetAsync(42);
            shop.Counter.Calls.Should().Be(1);
        }

        await shop.Reads.GetAsync(42);
        shop.Counter.Calls.Should().Be(2);
    }

    [Fact]
    public async Task An_ambient_transaction_invalidates_when_it_completes()
    {
        await using var shop = await Shop.CreateAsync();
        await shop.Reads.GetAsync(42);

        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using (var db = await shop.CreateDbAsync())
            {
                db.InvalidateAfterCommit("product:42");
                await db.SaveChangesAsync();
            }
            await shop.Reads.GetAsync(42);
            shop.Counter.Calls.Should().Be(1);
            scope.Complete();
        }

        await shop.Reads.GetAsync(42);
        shop.Counter.Calls.Should().Be(2);
    }

    [Fact]
    public async Task An_aborted_ambient_transaction_drops_the_tags()
    {
        await using var shop = await Shop.CreateAsync();
        await shop.Reads.GetAsync(42);

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var db = await shop.CreateDbAsync();
            db.InvalidateAfterCommit("product:42");
            await db.SaveChangesAsync();
        }

        await shop.Reads.GetAsync(42);
        shop.Counter.Calls.Should().Be(1);
    }
}
