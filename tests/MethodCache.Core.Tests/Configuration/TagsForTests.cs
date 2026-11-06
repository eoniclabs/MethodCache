using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

/// <summary>
/// <c>TagsFor</c> adds tags read from each result to its entry, next to the method's own tags, such as the customer of
/// an order found by its id.
/// </summary>
public class TagsForTests
{
    private static readonly string CountId = MethodIds.For(typeof(IOrders), nameof(IOrders.CountAsync));

    public sealed record Order(int Id, int CustomerId);

    public interface IOrders
    {
        [Cache(Duration = "1h", Tags = ["orders"])] Task<Order> GetAsync(int id);
        [Cache] Task<int> CountAsync(int id);
    }

    public sealed class Orders : IOrders
    {
        public int Calls { get; private set; }
        public int CustomerId { get; set; } = 7;

        public Task<Order> GetAsync(int id) { Calls++; return Task.FromResult(new Order(id, CustomerId)); }
        public Task<int> CountAsync(int id) => Task.FromResult(++Calls);
    }

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    [Fact]
    public async Task TagsFor_InvalidatingATagOfTheResult_RemovesTheEntry()
    {
        var (provider, service) = Build(mc => mc.Method((IOrders o) => o.GetAsync(Any<int>.Value))
            .TagsFor(order => [MethodCacheKeys.Tag($"customer:{order.CustomerId}")]));
        await using var _ = provider;
        var orders = provider.GetRequiredService<IOrders>();
        var cache = provider.GetRequiredService<ICacheManager>();

        await orders.GetAsync(42);
        await orders.GetAsync(42);
        await cache.InvalidateTagAsync("customer:8");
        await orders.GetAsync(42);
        await cache.InvalidateTagAsync("customer:7");
        await orders.GetAsync(42);

        Assert.Equal(2, service.Calls);
    }

    [Fact]
    public async Task TagsFor_KeepsTheMethodsOwnTags()
    {
        var (provider, service) = Build(mc => mc.Method((IOrders o) => o.GetAsync(Any<int>.Value))
            .TagsFor(order => [$"customer:{order.CustomerId}"]));
        await using var _ = provider;
        var orders = provider.GetRequiredService<IOrders>();

        await orders.GetAsync(42);
        await provider.GetRequiredService<ICacheManager>().InvalidateTagAsync("orders");
        await orders.GetAsync(42);

        Assert.Equal(2, service.Calls);
    }

    [Fact]
    public async Task TagsFor_ThrowingOrGivingAnEmptyTag_DoesNotCacheTheResult()
    {
        var (provider, service) = Build(mc =>
        {
            mc.Method((IOrders o) => o.GetAsync(Any<int>.Value)).TagsFor(_ => [" "]);
            mc.Method((IOrders o) => o.CountAsync(Any<int>.Value)).TagsFor(_ => throw new InvalidOperationException("no tags"));
        });
        await using var _ = provider;
        var orders = provider.GetRequiredService<IOrders>();

        await orders.GetAsync(1);
        await orders.GetAsync(1);
        await orders.CountAsync(1);
        await orders.CountAsync(1);

        Assert.Equal(4, service.Calls);
    }

    [Fact]
    public async Task ClearTagsInAHigherLayer_RemovesTagsFor()
    {
        var (provider, service) = Build(mc => mc.Method((IOrders o) => o.GetAsync(Any<int>.Value))
            .TagsFor(order => [$"customer:{order.CustomerId}"]));
        await using var _ = provider;
        await provider.GetRequiredService<IRuntimeCacheConfigurator>()
            .UpsertAsync(MethodIds.For(typeof(IOrders), nameof(IOrders.GetAsync)), o => o.ClearTags().AddTags("override"));
        var orders = provider.GetRequiredService<IOrders>();

        await orders.GetAsync(42);
        await provider.GetRequiredService<ICacheManager>().InvalidateTagAsync("customer:7");
        await orders.GetAsync(42);

        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public void TagsFor_ForAnotherResultType_FailsStartup()
    {
        var (provider, _) = Build(mc => mc.Method((IOrders o) => o.CountAsync(0)).Configure(o => o.TagsFor<string>(text => [text])));
        using var __ = provider;

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IPolicyRegistry>().GetAllPolicies());

        Assert.Contains("TagsFor<string> can't check the int result", error.Message, StringComparison.Ordinal);
        Assert.Contains(CountId, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TagsFor_InDefaults_Throws()
    {
        var error = Assert.ThrowsAny<InvalidOperationException>(() =>
        {
            var (provider, _) = Build(mc => mc.Defaults(o => o.TagsFor<int>(id => [$"id:{id}"])));
            using var __ = provider;
            provider.GetRequiredService<IPolicyRegistry>().GetAllPolicies();
        });

        Assert.Contains("TagsFor is set in", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_TagsOfAndCanCache_ReadTheResultsTags()
    {
        var options = new CacheEntryOptions.Builder().AddTags("orders")
            .TagsFor<Order>(order => [$"customer:{order.CustomerId}", "orders"]).Build();
        var invalid = new CacheEntryOptions.Builder().TagsFor<Order>(_ => [""]).Build();
        var cleared = new CacheEntryOptions.Builder().TagsFor<Order>(order => [$"customer:{order.CustomerId}"]).ClearTags().Build();

        Assert.Equal(["orders", "customer:7"], options.TagsOf(new Order(1, 7)));
        Assert.True(options.CanCache(new Order(1, 7)));
        Assert.False(invalid.CanCache(new Order(1, 7)));
        Assert.Empty(invalid.TagsOf(new Order(1, 7)));
        Assert.Empty(cleared.TagsOf(new Order(1, 7)));
        Assert.Throws<InvalidOperationException>(() => options.TagsOf(42));
    }

    [Fact]
    public async Task GetOrCreateAsync_StoresTheResultsTags()
    {
        await using var provider = new ServiceCollection().AddSingleton<TimeProvider>(_time).AddMethodCache().BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheManager>();
        var calls = 0;
        ValueTask<Order> Load(CancellationToken _) => ValueTask.FromResult(new Order(++calls, 7));
        void Options(CacheEntryOptions.Builder o) => o.WithDuration(TimeSpan.FromMinutes(5)).TagsFor<Order>(order => [$"customer:{order.CustomerId}"]);

        await cache.GetOrCreateAsync("order:1", Load, Options);
        await cache.GetOrCreateAsync("order:1", Load, Options);
        await cache.InvalidateTagAsync("customer:7");
        var reloaded = await cache.GetOrCreateAsync("order:1", Load, Options);

        Assert.Equal(2, calls);
        Assert.Equal(2, reloaded.Id);
    }

    [Fact]
    public async Task SetAsync_StoresTheValuesTags()
    {
        await using var provider = new ServiceCollection().AddSingleton<TimeProvider>(_time).AddMethodCache().BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheManager>();

        await cache.SetAsync("order:1", new Order(1, 7), o => o.WithDuration(TimeSpan.FromMinutes(5))
            .TagsFor<Order>(order => [$"customer:{order.CustomerId}"]));
        await cache.InvalidateTagAsync("customer:7");

        Assert.False((await cache.TryGetAsync<Order>("order:1")).Found);
    }

    [Fact]
    public async Task TagInvalidatedWhileTheResultLoads_KeepsItFromBeingCached()
    {
        await using var provider = new ServiceCollection().AddSingleton<TimeProvider>(_time).AddMethodCache().BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheManager>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Options(CacheEntryOptions.Builder o) => o.WithDuration(TimeSpan.FromMinutes(5)).TagsFor<Order>(order => [$"customer:{order.CustomerId}"]);

        var load = cache.GetOrCreateAsync("order:1", async _ =>
        {
            started.SetResult();
            await gate.Task;
            return new Order(1, 7);
        }, Options).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cache.InvalidateTagAsync("customer:7");
        gate.SetResult();

        Assert.Equal(7, (await load.WaitAsync(TimeSpan.FromSeconds(10))).CustomerId);
        Assert.False((await cache.TryGetAsync<Order>("order:1")).Found);
    }

    private (ServiceProvider Provider, Orders Service) Build(Action<MethodCacheBuilder> configure)
    {
        var service = new Orders();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton<IOrders>(service);
        services.AddMethodCache(configure);
        return (services.BuildServiceProvider(), service);
    }
}
