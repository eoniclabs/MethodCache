using Docs.Conditional;
using Docs.Expiration;
using Docs.GettingStarted;
using Docs.GettingStarted.After;
using Docs.Invalidation;
using MyApp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace MethodCache.Docs.Tests;

// The feature pages' claims, checked against the code they show
public class FeatureTests(ITestOutputHelper output)
{
    private sealed class Users : MyApp.IUserService
    {
        public Task<MyApp.User> GetAsync(int id) => Task.FromResult(new MyApp.User(id, "a"));
        public Task<List<MyApp.User>> SearchAsync(UserQuery query) => Task.FromResult(new List<MyApp.User>());
        public Task<List<MyApp.User>> ByIdsAsync(int[] ids) => Task.FromResult(new List<MyApp.User>());
        public Task<int> CountAsync(Dictionary<string, int[]> filters) => Task.FromResult(filters.Count);
    }

    [Fact]
    public async Task Keys_AreBuiltFromTheMethodAndItsArguments()
    {
        var services = new ServiceCollection();
        services.AddSingleton<MyApp.IUserService, Users>();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        var users = provider.GetRequiredService<MyApp.IUserService>();

        await users.GetAsync(42);
        await users.SearchAsync(new UserQuery("alice", 2));
        await users.ByIdsAsync([1, 2, 3]);
        await users.CountAsync(new Dictionary<string, int[]> { ["age"] = [30, 40] });

        var keys = provider.GetRequiredService<ICacheAdministrator>().ListEntries(storedKeyPrefix: "[MethodCache.Docs.Tests]MyApp.").Select(e => e.StoredKey).ToList();
        foreach (var key in keys)
        {
            output.WriteLine(key);
        }

        Assert.Contains("[MethodCache.Docs.Tests]MyApp.IUserService.GetAsync:42", keys);
        Assert.Contains("[MethodCache.Docs.Tests]MyApp.IUserService.SearchAsync:(alice:2)", keys);
        Assert.Contains("[MethodCache.Docs.Tests]MyApp.IUserService.ByIdsAsync:[3:1:2:3]", keys);
        Assert.Matches(@"^\[MethodCache\.Docs\.Tests\]MyApp\.IUserService\.CountAsync:[0-9a-f]{32}$", Assert.Single(keys, key => key.Contains(".CountAsync:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task GettingStarted_FactoryRegistrationIsCachedOnceWithItsLifetime()
    {
        var created = 0;
        var services = new ServiceCollection();
        services.AddSingleton<Database>();
        services.AddScoped<global::Docs.GettingStarted.After.IProductService>(sp =>
        {
            created++;
            return new global::Docs.GettingStarted.After.ProductService(sp.GetRequiredService<Database>());
        });
        services.AddMethodCache();

        var registration = Assert.Single(services, d => d.ServiceType == typeof(global::Docs.GettingStarted.After.IProductService) && !d.IsKeyedService);
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var products = scope.ServiceProvider.GetRequiredService<global::Docs.GettingStarted.After.IProductService>();

        Assert.IsNotType<global::Docs.GettingStarted.After.ProductService>(products);
        Assert.Single(scope.ServiceProvider.GetServices<global::Docs.GettingStarted.After.IProductService>());
        Assert.Equal(1, created);
    }

    private sealed class Invoices : IInvoiceService
    {
        public Task<Invoice> GetAsync(int id) => Task.FromResult(new Invoice(id));
        public Task<Invoice> FindAsync(int id) => Task.FromResult(new Invoice(id));
        public Task<Invoice> FindAsync(string number) => Task.FromResult(new Invoice(7));
    }

    [Fact]
    public async Task Keys_MethodCacheKeysMatchVersionedAndOverloadedMethods()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInvoiceService, Invoices>();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        var invoices = provider.GetRequiredService<IInvoiceService>();
        await invoices.GetAsync(42);
        await invoices.FindAsync("INV-7");

        var keys = provider.GetRequiredService<ICacheAdministrator>().ListEntries(storedKeyPrefix: "[MethodCache.Docs.Tests]MyApp.IInvoiceService").Select(e => e.StoredKey).ToList();
        var (versioned, overload) = KeyExamples.Build(provider.GetRequiredService<IMethodCacheKeys>());
        Assert.Equal("[MethodCache.Docs.Tests]MyApp.IInvoiceService.GetAsync:v2:42", versioned);
        Assert.Equal("[MethodCache.Docs.Tests]MyApp.IInvoiceService.FindAsync(string):INV-7", overload);
        Assert.Contains(versioned, keys);
        Assert.Contains(overload, keys);
        var entry = Assert.Single(KeyExamples.ListMethod(provider.GetRequiredService<IMethodCacheKeys>(), provider.GetRequiredService<ICacheAdministrator>()));
        Assert.Equal(overload, entry.StoredKey);
        Assert.Equal("[MethodCache.Docs.Tests]MyApp.IInvoiceService.FindAsync(string)", entry.MethodId);
    }

    private sealed class Documents : IDocumentStore
    {
        public Task<byte[]> GetAsync(string key) => Task.FromResult(new byte[] { 1 });
    }

    [Fact]
    public async Task Keys_ArgumentsCannotReplaceTheMethodScope()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDocumentStore, Documents>();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IDocumentStore>().GetAsync("doc:2026/report.pdf");

        var keys = provider.GetRequiredService<ICacheAdministrator>().ListEntries().Select(e => e.StoredKey).ToList();
        Assert.Contains(provider.GetRequiredService<IMethodCacheKeys>().For<IDocumentStore>(store => store.GetAsync("doc:2026/report.pdf")), keys);
        Assert.DoesNotContain("doc:2026/report.pdf", keys);
    }

    private sealed class Orders : IOrderService
    {
        public int Loads;
        public Task<Order> GetAsync(int orderId) { Loads++; return Task.FromResult(new Order(orderId, 7, 10m)); }
        public Task<List<Order>> ListForCustomerAsync(int customerId) { Loads++; return Task.FromResult(new List<Order>()); }
        public Task CancelAsync(int orderId, int customerId) => Task.CompletedTask;
        public Task ImportAsync(Stream file) => Task.CompletedTask;
    }

    private static (ServiceProvider Provider, Orders Orders, IOrderService Service) BuildOrders()
    {
        var orders = new Orders();
        var services = new ServiceCollection();
        services.AddSingleton<IOrderService>(orders);
        services.AddMethodCache();
        var provider = services.BuildServiceProvider();
        return (provider, orders, provider.GetRequiredService<IOrderService>());
    }

    private static async Task WarmAsync(IOrderService service)
    {
        await service.GetAsync(42);
        await service.GetAsync(43);
        await service.ListForCustomerAsync(7);
    }

    private sealed class Shapes : IShapeService
    {
        public int Loads;
        public Task<int> CountAsync(object[] items) { Loads++; return Task.FromResult(items.Length); }
        public Task<int> AreaAsync(ShapeQuery query) { Loads++; return Task.FromResult(0); }
    }

    [Fact]
    public async Task Keys_NestedValuesKeepTheirRuntimeType()
    {
        var shapes = new Shapes();
        var services = new ServiceCollection();
        services.AddSingleton<IShapeService>(shapes);
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IShapeService>();

        await service.CountAsync([1]);
        await service.CountAsync([1L]);
        await service.CountAsync([1]);
        await service.AreaAsync(new ShapeQuery(new Circle("c", 1)));
        await service.AreaAsync(new ShapeQuery(new Circle("c", 2)));
        await service.AreaAsync(new ShapeQuery(new Circle("c", 1)));

        Assert.Equal(4, shapes.Loads);   // 1 and 1L, and radius 1 and 2, are different keys; the repeats are hits
    }

    [Fact]
    public async Task Invalidation_TagTemplatesRemoveOnlyTheMatchingEntries()
    {
        var (provider, orders, service) = BuildOrders();
        await using var _ = provider;
        await WarmAsync(service);

        await service.CancelAsync(42, 7);
        await WarmAsync(service);
        Assert.Equal(5, orders.Loads);   // order 42 and customer 7's list reloaded, order 43 still cached

        await service.ImportAsync(Stream.Null);
        await WarmAsync(service);
        Assert.Equal(8, orders.Loads);
    }

    [Fact]
    public async Task Invalidation_FromCode_ByTagKeyAndPattern()
    {
        var (provider, orders, service) = BuildOrders();
        await using var _ = provider;
        var cache = provider.GetRequiredService<ICacheManager>();
        await WarmAsync(service);

        await global::Docs.Invalidation.FromCode.Tags(cache);
        await WarmAsync(service);
        Assert.Equal(5, orders.Loads);

        await global::Docs.Invalidation.FromCode.Keys(cache, provider.GetRequiredService<IMethodCacheKeys>());
        await WarmAsync(service);
        Assert.Equal(6, orders.Loads);

        await global::Docs.Invalidation.FromCode.Pattern(cache);
        await WarmAsync(service);
        Assert.Equal(7, orders.Loads);
    }

    private sealed class Products : global::Docs.Conditional.IProductService
    {
        public int Searches;
        public int Finds;
        public Task<List<global::Docs.Conditional.Product>> SearchAsync(string text)
        {
            Searches++;
            return Task.FromResult(text == "tea" ? new List<global::Docs.Conditional.Product> { new(1, "Tea") } : new List<global::Docs.Conditional.Product>());
        }

        public Task<global::Docs.Conditional.Product?> FindAsync(int id)
        {
            Finds++;
            return Task.FromResult<global::Docs.Conditional.Product?>(id == 1 ? new(1, "Tea") : null);
        }
    }

    [Fact]
    public async Task CacheWhen_SkipsStoringRejectedResults()
    {
        var products = new Products();
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Services.AddSingleton<global::Docs.Conditional.IProductService>(products);
        global::Docs.Conditional.Setup.Register(builder);
        await using var app = builder.Build();
        var service = app.Services.GetRequiredService<global::Docs.Conditional.IProductService>();

        await service.SearchAsync("tea");
        await service.SearchAsync("tea");
        await service.SearchAsync("coffee");
        await service.SearchAsync("coffee");
        Assert.Equal(3, products.Searches);

        await service.FindAsync(1);
        await service.FindAsync(1);
        await service.FindAsync(2);
        await service.FindAsync(2);
        Assert.Equal(3, products.Finds);
    }

    [Fact]
    public async Task Testing_NoOpCacheManagerTurnsCachingOff()
    {
        await using var cached = (ServiceProvider)global::Docs.Testing.Examples.WithCaching();
        await using var uncached = (ServiceProvider)global::Docs.Testing.Examples.WithoutCaching();

        Assert.Equal(1, await CountLoadsAsync(cached));
        Assert.Equal(2, await CountLoadsAsync(uncached));
    }

    private static async Task<int> CountLoadsAsync(IServiceProvider provider)
    {
        var loads = 0;
        var cache = provider.GetRequiredService<ICacheManager>();
        for (var i = 0; i < 2; i++)
        {
            await cache.GetOrCreateAsync("k", _ => { loads++; return ValueTask.FromResult(1); });
        }

        using var scope = provider.CreateScope();
        var products = scope.ServiceProvider.GetRequiredService<global::Docs.GettingStarted.After.IProductService>();
        Assert.Equal(await products.GetAsync(1), await products.GetAsync(1));
        return loads;
    }
}

public abstract record Shape(string Name);

public sealed record Circle(string Name, int Radius) : Shape(Name);

// A base-typed property: the key holds the Circle's own members too
public sealed record ShapeQuery(Shape Shape);

public interface IShapeService
{
    [Cache] Task<int> CountAsync(object[] items);
    [Cache] Task<int> AreaAsync(ShapeQuery query);
}
