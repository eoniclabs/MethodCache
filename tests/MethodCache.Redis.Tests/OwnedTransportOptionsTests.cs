using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace MethodCache.Redis.Tests;

public class OwnedTransportOptionsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("unknownMethodCacheSetting=true")]
    public void InvalidConnectionSettings_AreRejectedWithoutConnecting(string configuration)
    {
        var services = new ServiceCollection();
        services.AddMethodCacheRedis(options => options.Configuration = configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RedisOptions>>().Value);
    }

    [Fact]
    public async Task Backplane_UsesFinalCorePrefix_AndLeavesApplicationServiceAlone()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        multiplexer.GetSubscriber(Arg.Any<object>()).Returns(subscriber);
        var applicationBackplane = Substitute.For<IBackplane>();
        var services = new ServiceCollection();
        services.AddSingleton(applicationBackplane);
        services.AddMethodCacheRedis(options => options.ConnectionMultiplexerFactory = _ => multiplexer);
        services.Configure<DistributedCacheOptions>(options => options.KeyPrefix = "configured-last:");
        await using var provider = services.BuildServiceProvider();
        var owned = provider.GetRequiredKeyedService<IBackplane>("MethodCache");
        Assert.Same(applicationBackplane, provider.GetRequiredService<IBackplane>());
        await owned.SubscribeAsync(_ => Task.CompletedTask);
        await subscriber.Received(1).SubscribeAsync(RedisChannel.Literal("configured-last:invalidations"),
            Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public void BoundPrefix_IsImmutableForAllOwnedServices()
    {
        var services = new ServiceCollection();
        services.AddMethodCacheRedis("localhost:6379");
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredKeyedService<IBackplane>("MethodCache");
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IOptions<DistributedCacheOptions>>().Value.KeyPrefix = "too-late:");
    }
}
