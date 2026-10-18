using Docs;
using Docs.Readme;
using FxVendor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace MethodCache.Docs.Tests;

// The README's claims, checked against the code it shows
public class ReadmeTests
{
    [Fact]
    public async Task QuickStart_CachesPerArgument_AndRenameInvalidatesOnlyThatUser()
    {
        var database = new InMemoryUserDatabase();
        var services = new ServiceCollection();
        services.AddSingleton<IUserDatabase>(database);
        services.AddScoped<IUserService, UserService>();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserService>();

        await users.GetUserAsync(42);
        await users.GetUserAsync(42);
        await users.GetUserAsync(1);
        Assert.Equal(2, database.Loads);

        await users.RenameAsync(42, "Grace Brewster Hopper");
        Assert.Equal("Grace Brewster Hopper", (await users.GetUserAsync(42)).Name);
        await users.GetUserAsync(1);
        Assert.Equal(3, database.Loads);
    }

    [Fact]
    public async Task ExplicitFactory_PreservesScopedLifetime_AndCachesMethodCalls()
    {
        var database = new InMemoryUserDatabase();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IUserDatabase>(database);
        Setup.ExplicitFactory(builder);
        await using var provider = builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IUserService>();
        Assert.Same(first, firstScope.ServiceProvider.GetRequiredService<IUserService>());
        var second = secondScope.ServiceProvider.GetRequiredService<IUserService>();
        Assert.NotSame(first, second);

        await first.GetUserAsync(42);
        await second.GetUserAsync(42);
        Assert.Equal(1, database.Loads);
    }

    [Fact]
    public async Task CacheInterface_CachesTheListedMethodOnly()
    {
        var client = new FxRatesClient();
        var services = new ServiceCollection();
        services.AddSingleton<IFxRatesClient>(client);
        // Both the console tour and this project generate a module for the vendor interface.
        services.AddMethodCache(cache => cache.FromAssemblyOf<ReadmeTests>());
        await using var provider = services.BuildServiceProvider();
        var fx = provider.GetRequiredService<IFxRatesClient>();

        await fx.GetRateAsync("EUR", "SEK");
        await fx.GetRateAsync("EUR", "SEK");
        await fx.PlaceOrderAsync("EUR", "SEK", 100);
        await fx.PlaceOrderAsync("EUR", "SEK", 100);

        Assert.Equal(1, client.RateCalls);
        Assert.Equal(2, client.OrderCalls);
    }
}
