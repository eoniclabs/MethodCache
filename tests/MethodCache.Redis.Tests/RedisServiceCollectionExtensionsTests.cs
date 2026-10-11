using FluentAssertions;
using MethodCache.Core.Storage.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace MethodCache.Redis.Tests;

public class RedisServiceCollectionExtensionsTests
{
    [Fact]
    public void AddMethodCacheRedis_RegistersItsOwnCacheAndBackplane()
    {
        var services = new ServiceCollection();

        services.AddMethodCacheRedis("localhost:6379");
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredKeyedService<IDistributedCache>("MethodCache").Should().BeOfType<RedisDistributedCache>();
        provider.GetRequiredService<IBackplane>().Should().BeOfType<RedisBackplane>();
        provider.GetService<IDistributedCache>().Should().BeNull();
        provider.GetService<IConnectionMultiplexer>().Should().BeNull();
    }

    [Fact]
    public void AddMethodCacheRedis_LeavesTheAppsRedisCacheAlone()
    {
        var services = new ServiceCollection();
        services.AddStackExchangeRedisCache(options => options.InstanceName = "app");

        services.AddMethodCacheRedis("localhost:6379");
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IDistributedCache>().Should().BeAssignableTo<RedisCache>();
        var appOptions = provider.GetRequiredService<IOptions<RedisCacheOptions>>().Value;
        appOptions.InstanceName.Should().Be("app");
        appOptions.ConnectionMultiplexerFactory.Should().BeNull();
    }

    [Fact]
    public void AddMethodCacheRedis_WithoutBackplane_RegistersNoBackplane()
    {
        var services = new ServiceCollection();

        services.AddMethodCacheRedis(options => options.EnableBackplane = false);

        services.Should().NotContain(d => d.ServiceType == typeof(IBackplane));
    }

    [Fact]
    public async Task AddMethodCacheRedis_UsesTheConnectionFromTheFactory_WithoutDisposingIt()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var services = new ServiceCollection();
        services.AddSingleton(connection);

        services.AddMethodCacheRedis(options => options.ConnectionMultiplexerFactory = sp => sp.GetRequiredService<IConnectionMultiplexer>());
        var provider = services.BuildServiceProvider();

        (await provider.GetRequiredService<RedisConnection>().GetAsync()).Should().BeSameAs(connection);
        provider.GetRequiredService<RedisConnection>().Dispose();
        connection.DidNotReceive().Dispose();
    }

    [Fact]
    public async Task OwnConnection_StillOpeningAtDispose_IsClosedOnceOpen()
    {
        var opening = new TaskCompletionSource<IConnectionMultiplexer>();
        var connection = new RedisConnection(() => opening.Task, owned: true);
        var multiplexer = Substitute.For<IConnectionMultiplexer>();

        var pending = connection.GetAsync();
        connection.Dispose();
        opening.SetResult(multiplexer);
        await pending;

        multiplexer.Received(1).Dispose();
    }

    [Theory]
    [InlineData("app:", null, "app:invalidations")]
    [InlineData("methodcache:", null, "methodcache:invalidations")]
    [InlineData("app:", "custom", "custom")]
    public void BackplaneChannel_DefaultsToTheKeyPrefix(string prefix, string? channel, string expected)
    {
        new RedisOptions { KeyPrefix = prefix, BackplaneChannel = channel }.EffectiveBackplaneChannel.Should().Be(expected);
    }

    [Fact]
    public void AddMethodCacheRedis_AppliesKeyPrefix()
    {
        var services = new ServiceCollection();

        services.AddMethodCacheRedis(options => options.KeyPrefix = "app:");
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<MethodCache.Core.Configuration.DistributedCacheOptions>>().Value.KeyPrefix.Should().Be("app:");
    }

    [Fact]
    public void AddMethodCacheRedis_RejectsEmptyConfiguration()
    {
        var services = new ServiceCollection();

        var act = () => services.AddMethodCacheRedis(string.Empty);

        act.Should().Throw<ArgumentException>();
    }
}
