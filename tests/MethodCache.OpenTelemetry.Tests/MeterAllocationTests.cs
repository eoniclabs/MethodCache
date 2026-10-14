using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Extensibility;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using Xunit.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace MethodCache.OpenTelemetry.Tests;

[CollectionDefinition("Meter allocation", DisableParallelization = true)]
public class MeterAllocationCollection;

[Collection("Meter allocation")]
public class MeterAllocationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, 0), InlineData(true, 0), InlineData(false, 2), InlineData(true, 2), InlineData(false, 7), InlineData(true, 7)]
    public void Hit_CommonTags_DoesNotAllocate(bool listening, int additionalTags)
    {
        using var provider = new CacheMeterProvider(Options.Create(new OpenTelemetryOptions()));
        using var listener = listening ? Listen() : null;
        var tags = additionalTags > 0 ? Enumerable.Range(0, additionalTags).ToDictionary(i => $"custom{i}", i => (object?)$"value{i}") : null;
        for (var i = 0; i < 100; i++) provider.RecordCacheHit("M", tags);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) provider.RecordCacheHit("M", tags);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"1,000 hits, listening={listening}, additionalTags={additionalTags}: {allocated} bytes.");
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void DisabledMetrics_WithListener_DoNotEmitMeasurements()
    {
        using var provider = new CacheMeterProvider(Options.Create(new OpenTelemetryOptions { EnableMetrics = false }));
        using var listener = new MeterListener();
        var count = 0;
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Meter.Name == MetricInstruments.MeterName) active.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => count++);
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => count++);
        listener.Start();
        ICacheMetricsProvider metrics = provider;
        metrics.CacheHit("M");
        metrics.CacheMiss("M");
        metrics.CacheError("M", "private");
        metrics.CacheLatency("M", TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, count);
    }

    [Fact]
    public void ExportedTags_AndMeasurements_ArePreservedIncludingOverflowTags()
    {
        using var provider = new CacheMeterProvider(Options.Create(new OpenTelemetryOptions()));
        var counters = new List<(string Name, long Value, KeyValuePair<string, object?>[] Tags)>();
        var histograms = new List<(string Name, double Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Meter.Name == MetricInstruments.MeterName) active.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => counters.Add((instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => histograms.Add((instrument.Name, value, tags.ToArray())));
        listener.Start();
        var additional = Enumerable.Range(0, 12).ToDictionary(i => $"custom{i}", i => (object?)$"value{i}");
        provider.RecordCacheHit("M", additional);
        provider.RecordCacheMiss("M", additional);
        provider.RecordCacheError("M", "load_failed", additional);
        provider.RecordOperationDuration("M", 12.5, additional);
        provider.RecordKeyGenerationDuration("M", 0.5);
        Assert.Equal(3, counters.Count);
        Assert.All(counters, sample =>
        {
            Assert.Equal(1, sample.Value);
            Assert.Contains(new KeyValuePair<string, object?>("method", "M"), sample.Tags);
            Assert.All(additional, tag => Assert.Contains(tag, sample.Tags));
        });
        Assert.Contains(new KeyValuePair<string, object?>("error.type", "load_failed"), counters.Single(s => s.Name == MetricInstruments.Names.ErrorsTotal).Tags);
        var duration = Assert.Single(histograms, s => s.Name == MetricInstruments.Names.OperationDuration);
        Assert.Equal(12.5, duration.Value);
        Assert.All(additional, tag => Assert.Contains(tag, duration.Tags));
        Assert.Equal(0.5, histograms.Single(s => s.Name == MetricInstruments.Names.KeyGenerationDuration).Value);
    }

    [Fact]
    public void ConcurrentHits_KeepAllMeasurementsWithoutAllocatingInWorkers()
    {
        using var provider = new CacheMeterProvider(Options.Create(new OpenTelemetryOptions()));
        using var listener = new MeterListener();
        long total = 0;
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Name == MetricInstruments.Names.HitsTotal) active.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref total, value));
        listener.Start();
        var allocations = new long[4];
        Parallel.For(0, 4, worker =>
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10_000; i++) provider.RecordCacheHit("Concurrent");
            allocations[worker] = GC.GetAllocatedBytesForCurrentThread() - before;
        });
        Assert.Equal(40_000, total);
        Assert.All(allocations, bytes => Assert.Equal(0, bytes));
    }

    [Fact]
    public void InMemoryExporter_ReceivesHitTotals_WithMethodDimension()
    {
        using var provider = new CacheMeterProvider(Options.Create(new OpenTelemetryOptions()));
        var exported = new List<Metric>();
        using var sdk = Sdk.CreateMeterProviderBuilder().AddMeter(MetricInstruments.MeterName)
            .AddInMemoryExporter(exported).Build();
        var method = $"Exporter_{Guid.NewGuid():N}";
        for (var i = 0; i < 100; i++) provider.RecordCacheHit(method);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) provider.RecordCacheHit(method);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"1,000 hits with OpenTelemetry SDK and in-memory exporter: {allocated} bytes.");
        Assert.True(sdk.ForceFlush());
        long hits = 0;
        foreach (var metric in exported.Where(m => m.Name == MetricInstruments.Names.HitsTotal))
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                foreach (var tag in point.Tags)
                    if (tag.Key == "method" && Equals(tag.Value, method)) hits += point.GetSumLong();
            }
        }
        Assert.Equal(1100, hits);
    }

    [Fact]
    public void AListenerAddedLater_HitRatio_IncludesCallsMadeWithoutListeners()
    {
        // The cache counts every call itself, so the gauge reads its statistics instead of counting each hit
        using var provider = new CacheMeterProvider(Options.Create(new OpenTelemetryOptions()));
        var cache = new Mock<ICacheAdministrator>();
        cache.Setup(c => c.GetStatsAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<ICacheStats>(new CacheStats { Hits = 2, Misses = 1 }));
        provider.ObserveCache(cache.Object);
        using var listener = new MeterListener();
        double? ratio = null;
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Name == MetricInstruments.Names.HitRatio) active.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => ratio = value);
        listener.Start();
        listener.RecordObservableInstruments();
        Assert.Equal(2.0 / 3.0, ratio);
    }

    private static MeterListener Listen()
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Meter.Name == MetricInstruments.MeterName) active.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
        listener.Start();
        return listener;
    }
}
