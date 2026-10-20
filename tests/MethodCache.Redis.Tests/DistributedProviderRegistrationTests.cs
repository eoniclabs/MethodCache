using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace MethodCache.Redis.Tests;

/// <summary>MethodCache uses one distributed cache (L2), so UseRedis next to another provider is rejected.</summary>
public class DistributedProviderRegistrationTests
{
    [Fact]
    public void UseRedis_AfterAnotherDistributedCache_IsRejected()
    {
        var services = new ServiceCollection();
        services.AddMethodCacheDistributedCache(_ => Substitute.For<IDistributedCache>());

        var act = () => services.AddMethodCache(mc => mc.UseRedis("localhost:6379"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*one distributed cache*one from MethodCache.Redis.Tests and one from MethodCache.Redis.*");
    }

    [Fact]
    public void UseRedis_Twice_AddsToItsOptions()
    {
        var services = new ServiceCollection();
        services.AddMethodCache(mc => mc.UseRedis("localhost:6379"));

        services.AddMethodCache(mc => mc.UseRedis(options => options.EnableBackplane = false));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredKeyedService<IDistributedCache>("MethodCache").Should().BeOfType<RedisDistributedCache>();
    }
}
