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
        services.AddMethodCache(mc => mc.UseRedis(options => options.Configuration = configuration));
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RedisOptions>>().Value);
    }

    [Fact]
    public void UseRedis_WithoutConfigurationOrFactory_FailsStartupWithAClearMessage()
    {
        // A configuration section that is missing in this environment binds nothing
        var services = new ServiceCollection();
        services.AddMethodCache(mc => mc.UseRedis(_ => { }));
        using var provider = services.BuildServiceProvider();

        var error = StartupFailure(provider);

        Assert.Contains("RedisOptions.Configuration", error.Message);
        Assert.Contains("RedisOptions.ConnectionMultiplexerFactory", error.Message);
        Assert.Single(error.Failures);
    }

    [Fact]
    public void RedisOptions_HaveNoDefaultConfiguration()
    {
        Assert.Null(new RedisOptions().Configuration);
    }

    [Fact]
    public void InvalidConfiguration_FailsStartupWithoutRepeatingTheString()
    {
        var services = new ServiceCollection();
        services.AddMethodCache(mc => mc.UseRedis(options => options.Configuration = "password=secret-canary,unknownMethodCacheSetting=true"));
        using var provider = services.BuildServiceProvider();

        var error = StartupFailure(provider);

        Assert.Contains("not a valid StackExchange.Redis configuration string", error.Message);
        Assert.DoesNotContain("secret-canary", error.Message);
    }

    // What the host runs at startup; more than one validator may report the same options
    private static OptionsValidationException StartupFailure(IServiceProvider provider)
    {
        var error = Assert.ThrowsAny<Exception>(() => provider.GetRequiredService<IStartupValidator>().Validate());
        return error as OptionsValidationException
            ?? Assert.IsType<OptionsValidationException>(Assert.IsType<AggregateException>(error).InnerExceptions[0]);
    }

    [Fact]
    public void ConnectionFactoryWithoutConfiguration_PassesStartupValidation()
    {
        var services = new ServiceCollection();
        services.AddMethodCache(mc => mc.UseRedis(options => options.ConnectionMultiplexerFactory = _ => Substitute.For<IConnectionMultiplexer>()));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate();
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
        services.AddMethodCache(mc => mc.UseRedis(options => options.ConnectionMultiplexerFactory = _ => multiplexer));
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
        services.AddMethodCache(mc => mc.UseRedis("localhost:6379"));
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredKeyedService<IBackplane>("MethodCache");
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IOptions<DistributedCacheOptions>>().Value.KeyPrefix = "too-late:");
    }
}
