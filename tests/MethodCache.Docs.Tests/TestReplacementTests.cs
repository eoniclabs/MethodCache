using Docs.Testing;
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Database = Docs.GettingStarted.Database;
using IProductService = Docs.GettingStarted.After.IProductService;
using ProductService = Docs.GettingStarted.After.ProductService;

namespace MethodCache.Docs.Tests;

/// <summary>The testing guide's advice for a test that replaces a cached service with a fake after AddMethodCache.</summary>
public class TestReplacementTests
{
    private sealed class Users : MyApp.IUserService
    {
        public Task<MyApp.User> GetAsync(int id) => Task.FromResult(new MyApp.User(id, "a"));
        public Task<List<MyApp.User>> SearchAsync(MyApp.UserQuery query) => Task.FromResult(new List<MyApp.User>());
        public Task<List<MyApp.User>> ByIdsAsync(int[] ids) => Task.FromResult(new List<MyApp.User>());
        public Task<int> CountAsync(Dictionary<string, int[]> filters) => Task.FromResult(filters.Count);
    }

    private static ServiceCollection AppServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Database>();
        services.AddScoped<IProductService, ProductService>();
        services.AddSingleton<MyApp.IUserService, Users>();
        services.AddMethodCache();
        return services;
    }

    [Fact]
    public async Task FakeAddedAfterAddMethodCache_AndAddMethodCacheAgain_EveryServiceResolves()
    {
        var services = AppServices();
        services.AddScoped<IProductService, FakeProductService>();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var product = await scope.ServiceProvider.GetRequiredService<IProductService>().GetAsync(1);
        await scope.ServiceProvider.GetRequiredService<MyApp.IUserService>().GetAsync(1);

        Assert.Equal("Test tea", product.Name);
    }

    [Fact]
    public async Task FakeAddedAfterAddMethodCache_WithoutAddMethodCacheAgain_OtherServicesFailWithAnErrorThatSaysWhatToDo()
    {
        var services = AppServices();
        services.AddScoped<IProductService, FakeProductService>();
        await using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<MyApp.IUserService>());

        Assert.Contains("call AddMethodCache again after it", error.Message);
    }
}
