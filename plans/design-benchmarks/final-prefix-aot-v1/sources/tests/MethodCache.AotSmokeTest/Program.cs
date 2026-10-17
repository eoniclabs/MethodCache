using MethodCache;
using Microsoft.Extensions.DependencyInjection;
using Vendor.Rates;

// A third-party interface, cached without a wrapper
[assembly: CacheInterface(typeof(IRatesClient), nameof(IRatesClient.GetRateAsync), Duration = "00:01:00")]

// Runs the README Quick Start as a Native AOT executable. Exits with 1 when a call that should hit the cache
// reaches the implementation.
var services = new ServiceCollection();
services.AddScoped<IUserService, UserService>();
services.AddSingleton<IRatesClient, RatesClient>();
services.AddMethodCache(b => b.FromAssemblyOf<IPriceService>().AutoRegister(ServiceLifetime.Singleton));

await using var provider = services.BuildServiceProvider();

await using (var scope = provider.CreateAsyncScope())
{
    var users = scope.ServiceProvider.GetRequiredService<IUserService>();
    await users.GetUserAsync(42);
    await users.GetUserAsync(42);
}

await using (var scope = provider.CreateAsyncScope())
{
    var users = scope.ServiceProvider.GetRequiredService<IUserService>();
    await users.GetUserAsync(42);
}

// Explicit auto-registration supplies the otherwise unregistered price and catalog services.
var prices = provider.GetRequiredService<IPriceService>();
await prices.GetPriceAsync("ABC");
await prices.GetPriceAsync("ABC");
await prices.GenericAsync<int>(42);
await prices.GenericAsync<int>(42);

var rates = provider.GetRequiredService<IRatesClient>();
await rates.GetRateAsync("EUR", "SEK");
await rates.GetRateAsync("EUR", "SEK");
await rates.PlaceOrderAsync("book");
await rates.PlaceOrderAsync("book");

// Records of simple values, ICacheKeyProvider arguments, and arrays, lists and dictionaries of them are written into
// the key by the decorator. Other arguments go through the key generator, which uses reflection-based JSON and fails
// under Native AOT, so they are not used here (see docs/features/native-aot.md).
var catalog = provider.GetRequiredService<ICatalogService>();
await catalog.SearchAsync(new CatalogQuery("books", 2));
await catalog.SearchAsync(new CatalogQuery("books", 2));
await catalog.ByIdsAsync(new IdList(1, 2, 3));
await catalog.ByIdsAsync(new IdList(1, 2, 3));
await catalog.ManyAsync([1, 2, 3], [new CatalogQuery("books", 2)]);
await catalog.ManyAsync([1, 2, 3], [new CatalogQuery("books", 2)]);
await catalog.FilteredAsync(new Dictionary<string, int> { ["page"] = 2 });
await catalog.FilteredAsync(new Dictionary<string, int> { ["page"] = 2 });

var failures = new List<string>();
if (provider.GetRequiredService<IUserService>().GetType().Name != "IUserServiceDecorator")
{
    failures.Add("IUserService is not decorated");
}

if (UserService.Calls != 1)
{
    failures.Add($"IUserService implementation called {UserService.Calls} times, expected 1");
}

if (PriceService.Calls != 2)
{
    failures.Add($"IPriceService implementation called {PriceService.Calls} times, expected 2");
}

if (RatesClient.RateCalls != 1)
{
    failures.Add($"IRatesClient.GetRateAsync called {RatesClient.RateCalls} times, expected 1");
}

if (RatesClient.OrderCalls != 2)
{
    failures.Add($"IRatesClient.PlaceOrderAsync called {RatesClient.OrderCalls} times, expected 2 (it is not listed, so never cached)");
}

if (CatalogService.Calls != 4)
{
    failures.Add($"ICatalogService implementation called {CatalogService.Calls} times, expected 4");
}

// Previously compiled vendor code links against the current facade under Native AOT too.
var frozenServices = new ServiceCollection();
var frozenInner = new FrozenVendor.Contract1.Service();
frozenServices.AddSingleton<FrozenVendor.Contract1.IService>(frozenInner);
frozenServices.AddSingleton<FrozenVendor.ThirdParty.IClient, FrozenVendor.ThirdParty.Client>();
frozenServices.AddScoped<FrozenVendor.Contract1.IRepository<string>, FrozenVendor.Contract1.StringRepository>();
frozenServices.AddMethodCache(b => b.FromAssemblyOf<FrozenVendor.Contract1.Marker>());
await using (var frozenProvider = frozenServices.BuildServiceProvider())
{
    var frozen = frozenProvider.GetRequiredService<FrozenVendor.Contract1.IService>();
    if (frozen.Read(42) != 1 || frozen.Read(42) != 1 || await frozen.ReadAsync(42) != 2 || await frozen.ReadAsync(42) != 2)
        failures.Add("Frozen vendor return shapes did not preserve caching.");
    if (await frozen.ReadValueAsync(42) != 3 || await frozen.ReadValueAsync(42) != 3)
        failures.Add("Frozen vendor ValueTask did not preserve caching.");
    await frozen.UpdateAsync(42);
    if (frozen.Read(42) != 4) failures.Add("Frozen vendor tag invalidation failed.");
    var client = frozenProvider.GetRequiredService<FrozenVendor.ThirdParty.IClient>();
    if (await client.ReadAsync(42) != 1 || await client.ReadAsync(42) != 1)
        failures.Add("Frozen third-party CacheInterface did not preserve caching.");
    await using var scope = frozenProvider.CreateAsyncScope();
    var repository = scope.ServiceProvider.GetRequiredService<FrozenVendor.Contract1.IRepository<string>>();
    if (await repository.ReadAsync(42) != "1" || await repository.ReadAsync(42) != "1")
        failures.Add("Frozen closed generic interface did not preserve caching.");
}

// The historical net8 image uses materialized keys. This independently frozen
// net10 image also links the inline KeyBuilder constructor/ref lookup ABI.
var frozenNet10Services = new ServiceCollection();
frozenNet10Services.AddSingleton<FrozenVendor.Contract1Net10.IService, FrozenVendor.Contract1Net10.Service>();
frozenNet10Services.AddSingleton<FrozenVendor.ThirdPartyNet10.IClient, FrozenVendor.ThirdPartyNet10.Client>();
frozenNet10Services.AddScoped<FrozenVendor.Contract1Net10.IRepository<string>, FrozenVendor.Contract1Net10.StringRepository>();
frozenNet10Services.AddMethodCache(b => b.FromAssemblyOf<FrozenVendor.Contract1Net10.Marker>());
await using (var frozenNet10Provider = frozenNet10Services.BuildServiceProvider())
{
    var frozen = frozenNet10Provider.GetRequiredService<FrozenVendor.Contract1Net10.IService>();
    if (frozen.Read(42) != 1 || frozen.Read(42) != 1 || await frozen.ReadAsync(42) != 2 || await frozen.ReadAsync(42) != 2)
        failures.Add("Frozen net10 inline return shapes did not preserve caching.");
    if (await frozen.ReadValueAsync(42) != 3 || await frozen.ReadValueAsync(42) != 3)
        failures.Add("Frozen net10 inline ValueTask did not preserve caching.");
    await frozen.UpdateAsync(42);
    if (frozen.Read(42) != 4) failures.Add("Frozen net10 tag invalidation failed.");
    var client = frozenNet10Provider.GetRequiredService<FrozenVendor.ThirdPartyNet10.IClient>();
    if (await client.ReadAsync(42) != 1 || await client.ReadAsync(42) != 1)
        failures.Add("Frozen net10 third-party CacheInterface did not preserve caching.");
    await using var scope = frozenNet10Provider.CreateAsyncScope();
    var repository = scope.ServiceProvider.GetRequiredService<FrozenVendor.Contract1Net10.IRepository<string>>();
    if (await repository.ReadAsync(42) != "1" || await repository.ReadAsync(42) != "1")
        failures.Add("Frozen net10 closed generic interface did not preserve caching.");
}

try { await MethodCache.AotSmokeTest.PrefixCases.PrefixAotChecks.RunAsync(); }
catch (Exception error) { failures.Add("Generated prefix AOT check failed: " + error.Message); }

try { await HttpAotChecks.RunAsync(); }
catch (Exception error) { failures.Add("HTTP storage/admin AOT check failed: " + error.GetType().Name); }

foreach (var failure in failures)
{
    Console.Error.WriteLine($"FAIL: {failure}");
}

Console.WriteLine(failures.Count == 0 ? "AOT smoke test passed" : "AOT smoke test failed");
return failures.Count == 0 ? 0 : 1;

public interface IUserService
{
    [Cache(Duration = "00:10:00", Tags = new[] { "users", "user:{userId}" })]
    Task<string> GetUserAsync(int userId);
}

public sealed class UserService : IUserService
{
    private static int _calls;

    public static int Calls => _calls;

    public Task<string> GetUserAsync(int userId)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult($"user-{userId}");
    }
}

public interface IPriceService
{
    [Cache(Duration = "00:01:00")]
    Task<decimal> GetPriceAsync(string symbol);

    [Cache(Duration = "00:01:00")]
    Task<int> GenericAsync<T>(int id);
}

public sealed class PriceService : IPriceService
{
    private static int _calls;

    public static int Calls => _calls;

    public Task<decimal> GetPriceAsync(string symbol)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(42m);
    }

    public Task<int> GenericAsync<T>(int id)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(id);
    }
}

public sealed record CatalogQuery(string Term, int Page);

public sealed class IdList(params int[] ids) : ICacheKeyProvider
{
    public int Count => ids.Length;

    public string CacheKeyPart => string.Join(',', ids);
}

public interface ICatalogService
{
    [Cache(Duration = "00:01:00")]
    Task<int> SearchAsync(CatalogQuery query);

    [Cache(Duration = "00:01:00")]
    Task<int> ByIdsAsync(IdList ids);

    [Cache(Duration = "00:01:00")]
    Task<int> ManyAsync(int[] ids, List<CatalogQuery> queries);

    [Cache(Duration = "00:01:00")]
    Task<int> FilteredAsync(Dictionary<string, int> filters);
}

public sealed class CatalogService : ICatalogService
{
    private static int _calls;

    public static int Calls => _calls;

    public Task<int> SearchAsync(CatalogQuery query) => Count(query.Page);

    public Task<int> ByIdsAsync(IdList ids) => Count(ids.Count);

    public Task<int> ManyAsync(int[] ids, List<CatalogQuery> queries) => Count(ids.Length + queries.Count);

    public Task<int> FilteredAsync(Dictionary<string, int> filters) => Count(filters.Count);

    private static Task<int> Count(int value)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(value);
    }
}
