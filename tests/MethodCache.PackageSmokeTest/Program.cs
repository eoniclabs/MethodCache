using MethodCache;
using Microsoft.Extensions.DependencyInjection;

// The README Quick Start against the installed package. Exits with 1 when the second call reaches the
// implementation, which is what happens when the package does not bring its source generator.
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

var failures = new List<string>();
if (provider.GetRequiredService<IUserService>().GetType().Name != "IUserServiceDecorator")
{
    failures.Add("IUserService is not decorated, so the source generator did not run");
}

if (UserService.Calls != 1)
{
    failures.Add($"IUserService implementation called {UserService.Calls} times, expected 1");
}

foreach (var failure in failures)
{
    Console.Error.WriteLine($"FAIL: {failure}");
}

Console.WriteLine(failures.Count == 0 ? "Package smoke test passed" : "Package smoke test failed");
return failures.Count == 0 ? 0 : 1;

public interface IUserService
{
    [Cache(Duration = "00:10:00")]
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
