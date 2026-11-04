using FluentAssertions;
using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Redis.IntegrationTests;

/// <summary>
/// Two service providers sharing one Redis stand in for two application instances.
/// </summary>
/// <remarks>
/// A write tells the other instances to drop the key from memory. One that gets that message while it loads the key
/// doesn't trust what it read and loads the key itself, so an instance that must read another's value is created
/// after the write: pub/sub never delivers a message sent before the instance subscribed.
/// </remarks>
[Collection(RedisCollection.Name)]
public sealed class RedisDistributedCacheTests : IAsyncLifetime
{
    private static readonly TimeSpan Eventually = TimeSpan.FromSeconds(10);

    private readonly RedisFixture _redis;
    private readonly string _prefix = $"test:{Guid.NewGuid():N}:";
    private readonly List<ServiceProvider> _nodes = new();

    public RedisDistributedCacheTests(RedisFixture redis)
    {
        _redis = redis;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var node in _nodes)
        {
            await node.DisposeAsync();
        }
    }

    private async Task<ICacheManager> CreateNode()
    {
        var configuration = _redis.RequireConfiguration();
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.Configure<DistributedCacheOptions>(options => options.KeyPrefix = _prefix);
        services.AddMethodCache(mc => mc.UseRedis(options =>
        {
            options.Configuration = configuration;
        }));

        var provider = services.BuildServiceProvider();
        _nodes.Add(provider);

        // Redis connects in the background and fails commands until it has, which would send the node's first
        // calls to memory only; these tests are about two nodes that both reach Redis
        var connection = await provider.GetRequiredService<RedisConnection>().GetAsync();
        await WaitUntil(() => Task.FromResult(connection.IsConnected));
        // Establish pub/sub before the manager attaches its reconnect clear callback. These assertions
        // concern connected nodes, not a first pub/sub handshake retiring an in-flight initial load.
        await RedisFixture.WaitForSubscriberAsync(connection);

        var manager = provider.GetRequiredService<ICacheManager>();
        // Pub/sub keeps nothing for later: an invalidation sent before the node has subscribed never reaches it
        var backplane = provider.GetRequiredKeyedService<IBackplane>(MethodCache.Infrastructure.OwnedServices.Key);
        await WaitUntil(() => Task.FromResult(backplane.ConnectionState == BackplaneState.Connected));
        return manager;
    }

    private static async Task<string> Get(ICacheManager node, string key, string value, params string[] tags) =>
        await node.GetOrCreateAsync(key, _ => ValueTask.FromResult(value), options => options
            .WithDuration(TimeSpan.FromMinutes(5))
            .AddTags(tags));

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Eventually;
        while (!await condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the change should reach the other instance");
            await Task.Delay(50);
        }
    }

    [SkippableFact]
    public async Task SecondInstance_ReadsTheValueTheFirstStored()
    {
        var first = await CreateNode();
        await Get(first, "key", "from first");
        var second = await CreateNode(); // after the write: see the remarks above

        (await Get(second, "key", "from second")).Should().Be("from first");
    }

    [SkippableFact]
    public async Task TagInvalidation_ReachesTheOtherInstancesMemory()
    {
        var first = await CreateNode();
        await Get(first, "key", "v1", "users");
        var second = await CreateNode(); // after the write: see the remarks above
        (await Get(second, "key", "unused", "users")).Should().Be("v1");

        await first.InvalidateByTagsAsync("users");

        await WaitUntil(async () => await Get(second, "key", "v2", "users") == "v2");
    }

    [SkippableFact]
    public async Task KeyInvalidation_ReachesTheOtherInstancesMemory()
    {
        var first = await CreateNode();
        await Get(first, "key", "v1");
        var second = await CreateNode(); // after the write: see the remarks above
        (await Get(second, "key", "unused")).Should().Be("v1");

        await first.InvalidateByKeysAsync("key");

        await WaitUntil(async () => await Get(second, "key", "v2") == "v2");
    }
}
