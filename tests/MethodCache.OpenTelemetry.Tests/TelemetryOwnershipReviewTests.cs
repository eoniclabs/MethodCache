using OpenTelemetry;
using OpenTelemetry.Trace;
using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.OpenTelemetry.Tests;

public class TelemetryOwnershipReviewTests
{
    [Theory]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    public void InstrumentedManager_PreservesScopedAndTransientOwnership(ServiceLifetime lifetime)
    {
        IServiceCollection services = new ServiceCollection();
        var counter = new Counter();
        services.AddSingleton(counter);
        services.Add(new ServiceDescriptor(typeof(ICacheManager), typeof(SyncManager), lifetime));
        using var instrumentation = Sdk.CreateTracerProviderBuilder().AddMethodCacheInstrumentation().Build();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        for (var i = 0; i < 2; i++)
        {
            using var scope = provider.CreateScope();
            var first = scope.ServiceProvider.GetRequiredService<ICacheManager>();
            var second = scope.ServiceProvider.GetRequiredService<ICacheManager>();
            if (lifetime == ServiceLifetime.Scoped) Assert.Same(first, second);
            else Assert.NotSame(first, second);
        }
        Assert.Equal(lifetime == ServiceLifetime.Scoped ? 2 : 4, counter.Disposals);
    }

    [Theory]
    [InlineData("type", false)]
    [InlineData("factory", false)]
    [InlineData("instance", false)]
    [InlineData("type", true)]
    [InlineData("factory", true)]
    [InlineData("instance", true)]
    public async Task InstrumentedManager_PreservesContainerDisposalOwnership(string registration, bool asyncOnly)
    {
        var services = new ServiceCollection();
        var counter = new Counter();
        services.AddSingleton(counter);
        var type = asyncOnly ? typeof(AsyncManager) : typeof(SyncManager);
        ICacheManager Create() => asyncOnly ? new AsyncManager(counter) : new SyncManager(counter);
        if (registration == "type") services.AddSingleton(typeof(ICacheManager), type);
        else if (registration == "factory") services.AddSingleton<ICacheManager>(_ => Create());
        else services.AddSingleton(Create());
        using var instrumentation = Sdk.CreateTracerProviderBuilder().AddMethodCacheInstrumentation().Build();
        await using (var provider = services.BuildServiceProvider())
            _ = provider.GetRequiredService<ICacheManager>();
        Assert.Equal(registration == "instance" ? 0 : 1, counter.Disposals);
    }

    public sealed class Counter { public int Disposals; }
    public sealed class SyncManager(Counter counter) : NoOpCacheManager, IDisposable
    {
        public void Dispose() => counter.Disposals++;
    }
    public sealed class AsyncManager(Counter counter) : NoOpCacheManager, IAsyncDisposable
    {
        public ValueTask DisposeAsync() { counter.Disposals++; return default; }
    }
}
