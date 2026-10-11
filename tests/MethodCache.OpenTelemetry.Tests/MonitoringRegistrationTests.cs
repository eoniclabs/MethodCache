using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.OpenTelemetry.Tests;

/// <summary>
/// Exercises the real registration: AddMethodCache() followed by AddMethodCacheOpenTelemetry().
/// </summary>
public class MonitoringRegistrationTests
{
    private static readonly CacheRuntimePolicy Policy =
        CacheRuntimePolicy.FromPolicy("test", CachePolicy.Empty, CachePolicyFields.None);

    [Fact]
    public void AddMethodCacheOpenTelemetry_BeforeAddMethodCache_ThrowsWithGuidance()
    {
        var services = new ServiceCollection();

        var act = () => services.AddMethodCacheOpenTelemetry();

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddMethodCache()*before*");
    }

    [Fact]
    public void AddMethodCacheOpenTelemetry_WithDefaultOptions_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddMethodCache();

        var act = () => services.AddMethodCacheOpenTelemetry();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task AddMethodCacheOpenTelemetry_CalledTwice_WrapsCacheManagerOnce()
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.AddMethodCacheOpenTelemetry();
        services.AddMethodCacheOpenTelemetry();

        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<ICacheManager>();

        manager.Should().BeOfType<TelemetryCacheManager>();
        var inner = typeof(TelemetryCacheManager)
            .GetField("_innerManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(manager);
        inner.Should().BeOfType<InMemoryCacheManager>();
    }

    [Fact]
    public async Task Statistics_StayReachable_WhenCacheManagerIsInstrumented()
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.AddMethodCacheOpenTelemetry();

        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<ICacheManager>();
        var cache = provider.GetRequiredService<ICacheAdministrator>();

        await manager.GetOrCreateFastAsync("stats:1", "Stats", () => Task.FromResult("value"), Policy);
        var stats = await cache.GetStatsAsync();

        stats.Entries.Should().Be(1);
        stats.Misses.Should().Be(1);
    }

    [Fact]
    public async Task HitsAndMisses_AreCountedOnce_AndGaugesReadTheCache()
    {
        var methodName = $"Counted_{Guid.NewGuid():N}";
        using var recorder = new MeasurementRecorder();

        var services = new ServiceCollection();
        services.AddMethodCache();
        services.AddMethodCacheOpenTelemetry();

        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<ICacheManager>();

        // One miss followed by one hit through the instrumented manager, as a generated decorator's slow path does.
        await manager.GetOrCreateFastAsync("counted:1", methodName, () => Task.FromResult("value"), Policy);
        await manager.GetOrCreateFastAsync("counted:1", methodName, () => Task.FromResult("value"), Policy);

        recorder.RecordObservableInstruments();

        recorder.Sum(MetricInstruments.Names.MissesTotal, methodName).Should().Be(1);
        recorder.Sum(MetricInstruments.Names.HitsTotal, methodName).Should().Be(1);
        recorder.Count(MetricInstruments.Names.LoadDuration, methodName).Should().Be(1);
        recorder.Observed(MetricInstruments.Names.EntriesCount).Should().Contain(1);
    }

    private sealed class MeasurementRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentBag<(string Instrument, double Value, string? Method)> _measurements = new();

        public MeasurementRecorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == MetricInstruments.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.Start();
        }

        public void RecordObservableInstruments() => _listener.RecordObservableInstruments();

        public double Sum(string instrument, string method) =>
            _measurements.Where(m => m.Instrument == instrument && m.Method == method).Sum(m => m.Value);

        public int Count(string instrument, string method) =>
            _measurements.Count(m => m.Instrument == instrument && m.Method == method);

        public IEnumerable<double> Observed(string instrument) =>
            _measurements.Where(m => m.Instrument == instrument).Select(m => m.Value);

        public void Dispose() => _listener.Dispose();

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string? method = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "method")
                {
                    method = tag.Value as string;
                }
            }

            _measurements.Add((instrument.Name, value, method));
        }
    }
}
