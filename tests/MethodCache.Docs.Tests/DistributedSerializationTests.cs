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

    public class Animal
    {
        public string Name { get; set; } = "";
    }

    public sealed class Dog : Animal
    {
        public string Breed { get; set; } = "";
    }

    private readonly MethodCache.TestSupport.AtomicTestCache _shared;
    private readonly FakeTimeProvider _time = new();

    public DistributedSerializationTests() => _shared = new(_time);

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

    [Fact]
    public async Task CollectionsDeclaredAsInterfaces_AreShared()
    {
        using var first = Instance();
        using var second = Instance();
        await first.GetRequiredService<ICacheManager>().GetOrCreateAsync<IReadOnlyList<int>>("ids", (_, _) => ValueTask.FromResult<IReadOnlyList<int>>(new[] { 1, 2, 3 }));

        var read = await second.GetRequiredService<ICacheManager>().GetOrCreateAsync<IReadOnlyList<int>>(
            "ids", (_, _) => throw new InvalidOperationException("read from the distributed cache"));

        Assert.Equal(new[] { 1, 2, 3 }, read);
    }

    [Fact]
    public async Task DerivedValues_StayInMemory_EvenAfterBaseValuesWereShared()
    {
        using var first = Instance();
        using var second = Instance();
        var cache = first.GetRequiredService<ICacheManager>();
        await cache.GetOrCreateAsync<Animal>("cat", (_, _) => ValueTask.FromResult(new Animal { Name = "cat" }));
        await cache.GetOrCreateAsync<Animal>("rex", (_, _) => ValueTask.FromResult<Animal>(new Dog { Name = "rex", Breed = "collie" }));

        var read = await second.GetRequiredService<ICacheManager>().GetOrCreateAsync<Animal>(
            "rex", (_, _) => ValueTask.FromResult<Animal>(new Dog { Name = "rex", Breed = "collie" }));

        Assert.Equal("collie", Assert.IsType<Dog>(read).Breed);
    }
}
