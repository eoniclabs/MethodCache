using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MethodCache.EntityFrameworkCore.Tests;

/// <summary>
/// A transaction passed in with UseTransaction is committed by its owner. The reads here run on their own
/// connections to a database file, so they see only committed rows, as with a database server.
/// </summary>
public sealed class ExternalTransactionTests
{
    [Fact]
    public async Task Disposing_the_context_before_the_owner_commits_invalidates_nothing_early()
    {
        await using var shop = await Shop.CreateAsync(Registration.File);
        (await shop.Reads.GetAsync(42))!.Name.Should().Be("Hammer");

        await using (var connection = new SqliteConnection(shop.ConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
            await using (var db = await shop.CreateDbAsync())
            {
                db.Database.SetDbConnection(connection);
                await db.Database.UseTransactionAsync(transaction);
                (await db.Products.SingleAsync(p => p.Id == 42)).Name = "After";
                await db.SaveChangesAsync();
            }

            // Not committed: a read that went to the database now would cache "Hammer" again
            (await shop.Reads.GetAsync(42))!.Name.Should().Be("Hammer");
            shop.Counter.Calls.Should().Be(1);

            await transaction.CommitAsync();
        }

        // The connection closed, so the transaction is over
        (await shop.Reads.GetAsync(42))!.Name.Should().Be("After");
    }

    [Fact]
    public async Task A_context_disposed_after_the_owner_committed_invalidates_at_once()
    {
        await using var shop = await Shop.CreateAsync(Registration.File);
        await shop.Reads.GetAsync(42);

        await using var connection = new SqliteConnection(shop.ConnectionString);
        await connection.OpenAsync();
        await using (var db = await shop.CreateDbAsync())
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
            db.Database.SetDbConnection(connection);
            await db.Database.UseTransactionAsync(transaction);
            (await db.Products.SingleAsync(p => p.Id == 42)).Name = "After";
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        // The connection stays open, but the transaction has ended
        (await shop.Reads.GetAsync(42))!.Name.Should().Be("After");
    }

    [Fact]
    public async Task A_connected_context_that_commits_the_transaction_invalidates_what_another_context_saved_in_it()
    {
        await using var shop = await Shop.CreateAsync(Registration.File);
        await shop.Reads.GetAsync(42);

        await using var owner = await shop.CreateDbAsync();
        await using var transaction = await owner.Database.BeginTransactionAsync();
        await using (var participant = await shop.CreateDbAsync())
        {
            participant.Database.SetDbConnection(owner.Database.GetDbConnection());
            await participant.Database.UseTransactionAsync(transaction.GetDbTransaction());
            (await participant.Products.SingleAsync(p => p.Id == 42)).Name = "Shared";
            await participant.SaveChangesAsync();
        }
        (await shop.Reads.GetAsync(42))!.Name.Should().Be("Hammer");

        await transaction.CommitAsync();

        (await shop.Reads.GetAsync(42))!.Name.Should().Be("Shared");
    }

    [Fact]
    public async Task A_rollback_through_a_connected_context_drops_the_tags()
    {
        await using var shop = await Shop.CreateAsync(Registration.File);
        await shop.Reads.GetAsync(42);

        await using var owner = await shop.CreateDbAsync();
        await using (var transaction = await owner.Database.BeginTransactionAsync())
        {
            await using (var participant = await shop.CreateDbAsync())
            {
                participant.Database.SetDbConnection(owner.Database.GetDbConnection());
                await participant.Database.UseTransactionAsync(transaction.GetDbTransaction());
                (await participant.Products.SingleAsync(p => p.Id == 42)).Name = "Rolled back";
                await participant.SaveChangesAsync();
            }
            await transaction.RollbackAsync();
        }

        (await shop.Reads.GetAsync(42))!.Name.Should().Be("Hammer");
        shop.Counter.Calls.Should().Be(1);
    }
}
