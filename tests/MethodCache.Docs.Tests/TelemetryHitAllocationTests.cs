using System.Diagnostics.Metrics;
using MethodCache.Extensibility;
using MethodCache.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests;

[CollectionDefinition("Telemetry hit allocation", DisableParallelization = true)]
public class TelemetryHitAllocationCollection;

[Collection("Telemetry hit allocation")]
public class TelemetryHitAllocationTests
{
    [Fact]
    public async Task RegisteredMetricProducer_NormalHitTag_DoesNotAllocateWithAListener()
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.AddMethodCacheOpenTelemetry(options => options.EnableTracing = false);
        await using var provider = services.BuildServiceProvider();
        var metrics = provider.GetRequiredService<ICacheMetricsProvider>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Name == MetricInstruments.Names.HitsTotal) active.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
        listener.Start();
        for (var i = 0; i < 100; i++) metrics.CacheHit("Docs");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) metrics.CacheHit("Docs");
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
