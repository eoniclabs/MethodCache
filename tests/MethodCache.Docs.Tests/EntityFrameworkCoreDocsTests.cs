using Docs.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MethodCache;
using Xunit;

namespace MethodCache.Docs.Tests;

/// <summary>Runs the examples on docs/features/entity-framework-core.md against SQLite.</summary>
public sealed class EntityFrameworkCoreDocsTests : IAsyncLifetime
{
    private readonly SqliteConnection _keepAlive = new("DataSource=efcore-docs;Mode=Memory;Cache=Shared");
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        await _keepAlive.OpenAsync();
        var builder = WebApplication.CreateSlimBuilder();
        Setup.Register(builder, _keepAlive.ConnectionString);
        _app = builder.Build();
        await _app.StartAsync();
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
        await db.Database.EnsureCreatedAsync();
        db.Products.AddRange(
            new Product { Id = 1, Name = "Hammer", Price = 10m, CategoryId = 1 },
            new Product { Id = 2, Name = "Saw", Price = 20m, CategoryId = 1 });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private Task InScopeAsync(Func<IServiceProvider, Task> action)
        => InScopeAsync<bool>(async services => { await action(services); return true; });

    [Fact]
    public async Task SaveChanges_invalidates_the_mapped_tags()
    {
        Assert.Equal(10m, (await InScopeAsync(s => s.GetRequiredService<IProductReads>().GetAsync(1)))!.Price);

        await InScopeAsync(s => ProductEndpoints.ChangePriceAsync(1, 12m, s.GetRequiredService<ShopDb>(), CancellationToken.None));

        Assert.Equal(12m, (await InScopeAsync(s => s.GetRequiredService<IProductReads>().GetAsync(1)))!.Price);
    }

    [Fact]
    public async Task A_commit_invalidates_the_old_and_the_new_category()
    {
        Assert.Equal(2, (await InScopeAsync(s => s.GetRequiredService<IProductReads>().ListAsync(1))).Count);
        Assert.Empty(await InScopeAsync(s => s.GetRequiredService<IProductReads>().ListAsync(2)));

        await InScopeAsync(s => ProductEndpoints.MoveAsync(2, 2, s.GetRequiredService<ShopDb>(), CancellationToken.None));

        Assert.Single(await InScopeAsync(s => s.GetRequiredService<IProductReads>().ListAsync(1)));
        Assert.Single(await InScopeAsync(s => s.GetRequiredService<IProductReads>().ListAsync(2)));
    }

    [Fact]
    public async Task InvalidateAfterCommit_covers_a_bulk_update()
    {
        Assert.Equal(10m, (await InScopeAsync(s => s.GetRequiredService<IProductReads>().ListAsync(1)))[0].Price);

        await InScopeAsync(s => ProductEndpoints.RecalculateAsync(s.GetRequiredService<ShopDb>(),
            s.GetRequiredService<IMethodCacheKeys>(), 1, CancellationToken.None));

        Assert.Equal(11m, (await InScopeAsync(s => s.GetRequiredService<IProductReads>().ListAsync(1)))[0].Price);
    }

    [Fact]
    public async Task A_committed_bulk_delete_is_invalidated_with_ICacheManager()
    {
        Assert.Equal(2, (await InScopeAsync(s => s.GetRequiredService<IProductReads>().ListAsync(1))).Count);

        await InScopeAsync(s => ProductEndpoints.DeleteCategoryAsync(s.GetRequiredService<ShopDb>(),
            s.GetRequiredService<ICacheManager>(), s.GetRequiredService<IMethodCacheKeys>(), 1, CancellationToken.None));

        Assert.Empty(await InScopeAsync(s => s.GetRequiredService<IProductReads>().ListAsync(1)));
    }
}
