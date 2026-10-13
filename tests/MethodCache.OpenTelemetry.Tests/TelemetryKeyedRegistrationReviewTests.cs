using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.OpenTelemetry.Tests;

public class TelemetryKeyedRegistrationReviewTests
{
    [Fact]
    public void Instrumentation_KeyedOnlyManager_RequiresAnUnkeyedManager()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ICacheManager>("other", new NoOpCacheManager());
        Assert.Throws<InvalidOperationException>(() => services.AddMethodCacheOpenTelemetry());
    }

    [Fact]
    public void Instrumentation_PreservesAnUnrelatedKeyedManager()
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        var custom = new NoOpCacheManager();
        services.AddKeyedSingleton<ICacheManager>("other", custom);
        services.AddMethodCacheOpenTelemetry();
        using var provider = services.BuildServiceProvider();
        Assert.IsType<TelemetryCacheManager>(provider.GetRequiredService<ICacheManager>());
        Assert.Same(custom, provider.GetRequiredKeyedService<ICacheManager>("other"));
    }
}
