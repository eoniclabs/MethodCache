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
services.AddMethodCache();

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

// Registered by AddMethodCache() itself, since the application did not register it
var prices = provider.GetRequiredService<IPriceService>();
await prices.GetPriceAsync("ABC");
await prices.GetPriceAsync("ABC");

var rates = provider.GetRequiredService<IRatesClient>();
await rates.GetRateAsync("EUR", "SEK");
await rates.GetRateAsync("EUR", "SEK");
await rates.PlaceOrderAsync("book");
await rates.PlaceOrderAsync("book");

// Records of simple values and ICacheKeyProvider arguments are written into the key by the decorator. Arrays and
// lists go through the key generator, which uses reflection-based JSON and fails under Native AOT, so they are not
// used here (see docs/features/native-aot.md).
var catalog = provider.GetRequiredService<ICatalogService>();
await catalog.SearchAsync(new CatalogQuery("books", 2));
await catalog.SearchAsync(new CatalogQuery("books", 2));
await catalog.ByIdsAsync(new IdList(1, 2, 3));
await catalog.ByIdsAsync(new IdList(1, 2, 3));

var failures = new List<string>();
if (provider.GetRequiredService<IUserService>().GetType().Name != "IUserServiceDecorator")
{
    failures.Add("IUserService is not decorated");
}

if (UserService.Calls != 1)
{
    failures.Add($"IUserService implementation called {UserService.Calls} times, expected 1");
}

if (PriceService.Calls != 1)
{
    failures.Add($"IPriceService implementation called {PriceService.Calls} times, expected 1");
}

if (RatesClient.RateCalls != 1)
{
    failures.Add($"IRatesClient.GetRateAsync called {RatesClient.RateCalls} times, expected 1");
}

if (RatesClient.OrderCalls != 2)
{
    failures.Add($"IRatesClient.PlaceOrderAsync called {RatesClient.OrderCalls} times, expected 2 (it is not listed, so never cached)");
}

if (CatalogService.Calls != 2)
{
    failures.Add($"ICatalogService implementation called {CatalogService.Calls} times, expected 2");
}

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
}

public sealed class CatalogService : ICatalogService
{
    private static int _calls;

    public static int Calls => _calls;

    public Task<int> SearchAsync(CatalogQuery query) => Count(query.Page);

    public Task<int> ByIdsAsync(IdList ids) => Count(ids.Count);

    private static Task<int> Count(int value)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(value);
    }
}
