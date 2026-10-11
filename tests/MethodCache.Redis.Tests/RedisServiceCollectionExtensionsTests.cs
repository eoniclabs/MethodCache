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
    public void AddMethodCacheRedis_RegistersDistributedCacheAndBackplane()
    {
        var services = new ServiceCollection();

        services.AddMethodCacheRedis("localhost:6379");

        services.Should().Contain(d => d.ServiceType == typeof(IDistributedCache));
        services.Should().Contain(d => d.ServiceType == typeof(IConnectionMultiplexer));
        services.Should().Contain(d => d.ServiceType == typeof(IBackplane));
    }

    [Fact]
    public void AddMethodCacheRedis_WithoutBackplane_RegistersNoBackplane()
    {
        var services = new ServiceCollection();

        services.AddMethodCacheRedis(options => options.EnableBackplane = false);

        services.Should().NotContain(d => d.ServiceType == typeof(IBackplane));
    }

    [Fact]
    public void AddMethodCacheRedis_ReusesRegisteredConnection()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var services = new ServiceCollection();
        services.AddSingleton(connection);

        services.AddMethodCacheRedis("unused:1234");
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IConnectionMultiplexer>().Should().BeSameAs(connection);
        var cacheOptions = provider.GetRequiredService<IOptions<RedisCacheOptions>>().Value;
        cacheOptions.ConnectionMultiplexerFactory.Should().NotBeNull();
        cacheOptions.ConnectionMultiplexerFactory!().Result.Should().BeSameAs(connection);
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
