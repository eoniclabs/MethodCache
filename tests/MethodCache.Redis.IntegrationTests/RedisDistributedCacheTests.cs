using FluentAssertions;
using MethodCache.Core.Infrastructure.Extensions;
using MethodCache.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Redis.IntegrationTests;

/// <summary>
/// Two service providers sharing one Redis stand in for two application instances.
/// </summary>
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

    private ICacheManager CreateNode()
    {
        var configuration = _redis.RequireConfiguration();
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.AddMethodCacheRedis(options =>
        {
            options.Configuration = configuration;
            options.KeyPrefix = _prefix;
            options.BackplaneChannel = _prefix + "invalidations";
        });

        var provider = services.BuildServiceProvider();
        _nodes.Add(provider);
        return provider.GetRequiredService<ICacheManager>();
    }

    private static async Task<string> Get(ICacheManager node, string key, string value, params string[] tags) =>
        await node.GetOrCreateAsync(key, (_, _) => ValueTask.FromResult(value), options => options
            .WithDuration(TimeSpan.FromMinutes(5))
            .WithTags(tags));

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
        var first = CreateNode();
        var second = CreateNode();

        await Get(first, "key", "from first");

        (await Get(second, "key", "from second")).Should().Be("from first");
    }

    [SkippableFact]
    public async Task TagInvalidation_ReachesTheOtherInstancesMemory()
    {
        var first = CreateNode();
        var second = CreateNode();
        await Get(first, "key", "v1", "users");
        (await Get(second, "key", "unused", "users")).Should().Be("v1");

        await first.InvalidateByTagsAsync("users");

        await WaitUntil(async () => await Get(second, "key", "v2", "users") == "v2");
    }

    [SkippableFact]
    public async Task KeyInvalidation_ReachesTheOtherInstancesMemory()
    {
        var first = CreateNode();
        var second = CreateNode();
        await Get(first, "key", "v1");
        (await Get(second, "key", "unused")).Should().Be("v1");

        await first.InvalidateByKeysAsync("key");

        await WaitUntil(async () => await Get(second, "key", "v2") == "v2");
    }
}
