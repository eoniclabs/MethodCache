using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests;

[CollectionDefinition("Telemetry hit allocation", DisableParallelization = true)]
public class TelemetryHitAllocationCollection;

[Collection("Telemetry hit allocation")]
public class TelemetryHitAllocationTests
{
    private static object? _allocationProbeSink;

    [Fact]
    public async Task PublicCacheHit_WithCoreListener_DoesNotAllocate()
    {
        Assert.Equal(0, await MeasurePublicHits());
    }

    [Fact]
    public async Task PublicHitProbe_DetectsEvenOneAllocationInsideItsMeasuredWindow()
    {
        try
        {
            var allocated = await MeasurePublicHits(static () => _allocationProbeSink = new object());
            Assert.True(allocated > 0);
            Assert.NotNull(_allocationProbeSink);
        }
        finally { _allocationProbeSink = null; }
    }

    private static async Task<long> MeasurePublicHits(Action? insideMeasuredWindow = null)
    {
        using var provider = new ServiceCollection().AddMethodCache().BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheManager>();
        Assert.IsType<MethodCache.Extensibility.InMemoryCacheManager>(cache).Engine.ForegroundMaintenance = true;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Name == "methodcache.requests" && ReferenceEquals(instrument.Meter.Scope, provider.GetRequiredService<IMeterFactory>())) active.EnableMeasurementEvents(instrument);
        };
        long requests = 0;
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => requests += value);
        listener.Start();
        var originLoads = 0;
        Func<CacheContext, CancellationToken, ValueTask<int>> factory = (_, _) =>
        {
            originLoads++;
            return ValueTask.FromResult(42);
        };
        for (var i = 0; i < 1000; i++) await cache.GetOrCreateAsync("Docs", factory);
        // Warm the entire probe, including its allocation counters and loop/JIT path.
        // The following single fixed window still requires exactly zero bytes; no retry/minimum.
        _ = MeasureHits(cache, factory);
        var beforeRequests = requests;
        var allocated = MeasureHits(cache, factory, insideMeasuredWindow);
        Assert.Equal(1000, requests - beforeRequests);
        Assert.Equal(1, originLoads);
        return allocated;
    }

    // The probe returns before invoking assertions; initialization of assertion helpers is not cache work.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureHits(ICacheManager cache, Func<CacheContext, CancellationToken, ValueTask<int>> factory,
        Action? insideMeasuredWindow = null)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        insideMeasuredWindow?.Invoke();
        for (var i = 0; i < 1000; i++)
        {
            var hit = cache.GetOrCreateAsync("Docs", factory);
            if (!hit.IsCompletedSuccessfully) throw new InvalidOperationException("A warmed local hit must complete synchronously.");
            _ = hit.Result;
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
