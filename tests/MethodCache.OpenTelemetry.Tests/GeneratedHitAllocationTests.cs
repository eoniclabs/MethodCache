using System.Diagnostics.Metrics;
using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.OpenTelemetry.Tests;

[Collection("Core SDK instrumentation")]
public class GeneratedHitAllocationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedSyncTaskAndValueTaskHits_AddNoTelemetryAllocations(bool listening)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAllocationShapes, AllocationShapes>();
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();
        // Keep engine maintenance inline so worker scheduling allocations do not contaminate the telemetry probe.
        Assert.IsType<InMemoryCacheManager>(provider.GetRequiredService<ICacheManager>()).Engine.ForegroundMaintenance = true;
        var service = provider.GetRequiredService<IAllocationShapes>();
        _ = await MeasureShapes(service); // warm all generated and measurement call sites
        var baseline = await MeasureShapes(service);
#if NET9_0_OR_GREATER
        Assert.Equal(0, baseline);
#endif
        long count = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, self) =>
            {
                if (listening && instrument.Name == "methodcache.requests" && ReferenceEquals(instrument.Meter.Scope, provider.GetRequiredService<IMeterFactory>()))
                    self.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref count, value));
        listener.Start();
        var allocated = await MeasureShapes(service);
        Assert.Equal(baseline, allocated); // .NET 8 materializes string keys; subscribing must add nothing.
        Assert.Equal(listening ? 30_000 : 0, count);
    }

    private static async ValueTask<long> MeasureShapes(IAllocationShapes service)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            _ = service.Read(42);
            _ = await service.ReadAsync(42);
            _ = await service.ReadValueAsync(42);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void ConcurrentGeneratedHits_PreserveEveryMeasurement_WithoutAddedWorkerAllocations()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAllocationShapes, AllocationShapes>();
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();
        // Keep engine maintenance inline so worker scheduling allocations do not contaminate the telemetry probe.
        Assert.IsType<InMemoryCacheManager>(provider.GetRequiredService<ICacheManager>()).Engine.ForegroundMaintenance = true;
        var service = provider.GetRequiredService<IAllocationShapes>();
        for (var i = 0; i < 1000; i++) _ = service.Read(42);
        var baseline = MeasureWorkers(service);
#if NET9_0_OR_GREATER
        Assert.All(baseline, bytes => Assert.Equal(0, bytes));
#endif
        long total = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, self) =>
            {
                if (instrument.Name == "methodcache.requests" && ReferenceEquals(instrument.Meter.Scope, provider.GetRequiredService<IMeterFactory>())) self.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref total, value));
        listener.Start();
        var allocated = MeasureWorkers(service);
        Assert.Equal(40_000, total);
        Assert.Equal(baseline, allocated);
    }

    private static long[] MeasureWorkers(IAllocationShapes service)
    {
        var allocated = new long[4];
        Parallel.For(0, 4, worker =>
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10_000; i++) _ = service.Read(42);
            allocated[worker] = GC.GetAllocatedBytesForCurrentThread() - before;
        });
        return allocated;
    }

}

public interface IAllocationShapes
{
    [Cache] int Read(int id);
    [Cache] Task<int> ReadAsync(int id);
    [Cache] ValueTask<int> ReadValueAsync(int id);
}
public sealed class AllocationShapes : IAllocationShapes
{
    public int Read(int id) => id;
    public Task<int> ReadAsync(int id) => Task.FromResult(id);
    public ValueTask<int> ReadValueAsync(int id) => ValueTask.FromResult(id);
}
