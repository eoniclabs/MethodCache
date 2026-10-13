using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Docs.Tests;

// docs/features/distributed-cache.md#serialization: what JSON carries is shared, what it can't stays in memory
public class DistributedSerializationTests
{
    public sealed class Account
    {
        public int Id { get; private set; }

        public static Account Create(int id) => new() { Id = id };
    }

    private readonly MethodCache.TestSupport.AtomicTestCache _shared = new();
    private readonly FakeTimeProvider _time = new();

    private ServiceProvider Instance()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddMethodCache();
        services.AddMethodCacheDistributedCache(_ => _shared);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ValueTuples_AreShared()
    {
        using var first = Instance();
        using var second = Instance();
        await first.GetRequiredService<ICacheManager>().GetOrCreateAsync("page", (_, _) => ValueTask.FromResult((Total: 7, Name: "x")));

        var read = await second.GetRequiredService<ICacheManager>().GetOrCreateAsync<(int Total, string Name)>(
            "page", (_, _) => throw new InvalidOperationException("read from the distributed cache"));

        Assert.Equal((7, "x"), read);
    }

    [Fact]
    public async Task ValuesJsonCantReadBack_StayInEachInstancesMemory()
    {
        using var first = Instance();
        using var second = Instance();
        await first.GetRequiredService<ICacheManager>().GetOrCreateAsync("account", (_, _) => ValueTask.FromResult(Account.Create(42)));

        var calls = 0;
        var read = await second.GetRequiredService<ICacheManager>().GetOrCreateAsync(
            "account", (_, _) => { calls++; return ValueTask.FromResult(Account.Create(42)); });

        Assert.Equal(1, calls);
        Assert.Equal(42, read.Id);
    }
}
