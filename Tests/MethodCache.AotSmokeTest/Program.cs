using MethodCache.Core;
using MethodCache.Core.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;

// Runs the README Quick Start as a Native AOT executable. Exits with 1 when a call that should hit the cache
// reaches the implementation.
var services = new ServiceCollection();
services.AddScoped<IUserService, UserService>();
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
