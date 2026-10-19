using FluentAssertions;
using MethodCache.Extensibility;
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
    public void UseRedis_RegistersItsOwnCacheAndBackplane()
    {
        var services = new ServiceCollection();

        services.AddMethodCache(mc => mc.UseRedis("localhost:6379"));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredKeyedService<IDistributedCache>("MethodCache").Should().BeOfType<RedisDistributedCache>();
        provider.GetRequiredKeyedService<IBackplane>("MethodCache").Should().BeOfType<RedisBackplane>();
        provider.GetService<IDistributedCache>().Should().BeNull();
        provider.GetService<IConnectionMultiplexer>().Should().BeNull();
    }

    [Fact]
    public void UseRedis_LeavesTheAppsRedisCacheAlone()
    {
        var services = new ServiceCollection();
        services.AddStackExchangeRedisCache(options => options.InstanceName = "app");

        services.AddMethodCache(mc => mc.UseRedis("localhost:6379"));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IDistributedCache>().Should().BeAssignableTo<RedisCache>();
        var appOptions = provider.GetRequiredService<IOptions<RedisCacheOptions>>().Value;
        appOptions.InstanceName.Should().Be("app");
        appOptions.ConnectionMultiplexerFactory.Should().BeNull();
    }

    [Fact]
    public void UseRedis_WithoutBackplane_RegistersNoBackplane()
    {
        var services = new ServiceCollection();

        services.AddMethodCache(mc => mc.UseRedis(options => options.EnableBackplane = false));

        using var provider = services.BuildServiceProvider();
        provider.GetKeyedService<IBackplane>("MethodCache").Should().BeNull();
    }

    [Fact]
    public async Task UseRedis_UsesTheConnectionFromTheFactory_WithoutDisposingIt()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var services = new ServiceCollection();
        services.AddSingleton(connection);

        services.AddMethodCache(mc => mc.UseRedis(options => options.ConnectionMultiplexerFactory = sp => sp.GetRequiredService<IConnectionMultiplexer>()));
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

    [Fact]
    public void UseRedis_AppliesKeyPrefix()
    {
        var services = new ServiceCollection();

        services.AddMethodCache(mc => mc.UseRedis("localhost:6379"));
        services.Configure<DistributedCacheOptions>(options => options.KeyPrefix = "app:");
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<MethodCache.DistributedCacheOptions>>().Value.KeyPrefix.Should().Be("app:");
    }

    [Fact]
    public void UseRedis_RejectsEmptyConfiguration()
    {
        var services = new ServiceCollection();

        var act = () => services.AddMethodCache(mc => mc.UseRedis(string.Empty));

        act.Should().Throw<ArgumentException>();
    }
}
