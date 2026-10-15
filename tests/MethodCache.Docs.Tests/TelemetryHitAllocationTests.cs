using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests;

[CollectionDefinition("Telemetry hit allocation", DisableParallelization = true)]
public class TelemetryHitAllocationCollection;

[Collection("Telemetry hit allocation")]
public class TelemetryHitAllocationTests
{
    [Fact]
    public async Task PublicCacheHit_WithCoreListener_DoesNotAllocate()
    {
        using var provider = new ServiceCollection().AddMethodCache().BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheManager>();
        Assert.IsType<MethodCache.Extensibility.InMemoryCacheManager>(cache).Engine.ForegroundMaintenance = true;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Name == "methodcache.requests" && ReferenceEquals(instrument.Meter.Scope, provider.GetRequiredService<IMeterFactory>())) active.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
        listener.Start();
        Func<CacheContext, CancellationToken, ValueTask<int>> factory = static (_, _) => ValueTask.FromResult(42);
        for (var i = 0; i < 100; i++) await cache.GetOrCreateAsync("Docs", factory);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) await cache.GetOrCreateAsync("Docs", factory);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
