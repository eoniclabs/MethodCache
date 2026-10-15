using OpenTelemetry;
using OpenTelemetry.Trace;
using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.OpenTelemetry.Tests;

public class TelemetryKeyedRegistrationReviewTests
{
    [Fact]
    public void Instrumentation_KeyedOnlyManager_DoesNotChangeRegistration()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ICacheManager>("other", new NoOpCacheManager());
        using var instrumentation = Sdk.CreateTracerProviderBuilder().AddMethodCacheInstrumentation().Build();
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<ICacheManager>());
        Assert.IsType<NoOpCacheManager>(provider.GetRequiredKeyedService<ICacheManager>("other"));
    }

    [Fact]
    public void Instrumentation_PreservesAnUnrelatedKeyedManager()
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        var custom = new NoOpCacheManager();
        services.AddKeyedSingleton<ICacheManager>("other", custom);
        using var instrumentation = Sdk.CreateTracerProviderBuilder().AddMethodCacheInstrumentation().Build();
        using var provider = services.BuildServiceProvider();
        Assert.IsType<MethodCache.Extensibility.InMemoryCacheManager>(provider.GetRequiredService<ICacheManager>());
        Assert.Same(custom, provider.GetRequiredKeyedService<ICacheManager>("other"));
    }
}
