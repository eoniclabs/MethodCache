using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using FluentAssertions;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace MethodCache.OpenTelemetry.Tests;

/// <summary>
/// Registration with keyed or custom managers, option composition, baggage clearing and metric precision. In the
/// non-parallel meter collection, since a listener in another test would enable this provider's instruments.
/// </summary>
[Collection("Meter allocation")]
public class TelemetryOptionsAndMetricsTests
{
    private static readonly CacheRuntimePolicy Policy =
        CacheRuntimePolicy.FromPolicy("test", CachePolicy.Empty, CachePolicyFields.None);

    public class DisposableNoOp : NoOpCacheManager, IDisposable
    {
        public static int Disposed;

        public DisposableNoOp() : base(null)
        {
        }

        public void Dispose() => Interlocked.Increment(ref Disposed);
    }

    [Fact]
    public async Task AddMethodCacheOpenTelemetry_KeyedCacheManagerRegistered_InstrumentsTheDefaultAndKeepsTheKeyedOne()
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.AddKeyedSingleton<ICacheManager>("nocache", (_, _) => new NoOpCacheManager());
        services.AddMethodCacheOpenTelemetry();

        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ICacheManager>().Should().BeOfType<TelemetryCacheManager>();
        provider.GetRequiredKeyedService<ICacheManager>("nocache").Should().BeOfType<NoOpCacheManager>();
    }

    [Fact]
    public async Task InstrumentedCacheManager_RegisteredByType_IsDisposedWithTheProvider()
    {
        DisposableNoOp.Disposed = 0;
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.AddSingleton<ICacheManager, DisposableNoOp>();
        services.AddMethodCacheOpenTelemetry();

        await using (var provider = services.BuildServiceProvider())
        {
            provider.GetRequiredService<ICacheManager>().Should().BeOfType<TelemetryCacheManager>();
        }

        DisposableNoOp.Disposed.Should().Be(1);
    }

    [Fact]
    public async Task LoadDuration_SubMillisecondLoad_IsNotRecordedAsZero()
    {
        var method = $"Load_{Guid.NewGuid():N}";
        using var recorder = new Recorder();
        var time = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddMethodCache();
        services.AddMethodCacheOpenTelemetry();
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<ICacheManager>();

        await manager.GetOrCreateFastAsync("load:1", method, () =>
        {
            time.Advance(TimeSpan.FromMicroseconds(900));
            return Task.FromResult("v");
        }, Policy);

        recorder.Values(MetricInstruments.Names.LoadDuration, method).Should().ContainSingle()
            .Which.Should().BeApproximately(0.9, 0.05);
    }

    [Fact]
    public async Task LoadDuration_OnePointNineMillisecondLoad_IsNotTruncated()
    {
        var method = $"Load_{Guid.NewGuid():N}";
        using var recorder = new Recorder();
        var time = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddMethodCache();
        services.AddMethodCacheOpenTelemetry();
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<ICacheManager>();

        await manager.GetOrCreateFastAsync("load:1", method, () =>
        {
            time.Advance(TimeSpan.FromMicroseconds(1900));
            return Task.FromResult("v");
        }, Policy);

        recorder.Values(MetricInstruments.Names.LoadDuration, method).Should().ContainSingle()
            .Which.Should().BeApproximately(1.9, 0.05);
    }

    [Fact]
    public async Task AddMethodCacheOpenTelemetry_MetricsDisabled_KeepsTheAppsOwnMetricsProvider()
    {
        var mine = new Mock<ICacheMetricsProvider>().Object;
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.AddSingleton(mine);
        services.AddMethodCacheOpenTelemetry(o => o.EnableMetrics = false);

        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ICacheMetricsProvider>().Should().BeSameAs(mine);
    }

    [Fact]
    public async Task AddMethodCacheOpenTelemetry_SecondCall_DoesNotResetOptionsTheFirstCallSet()
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.AddMethodCacheOpenTelemetry(o => o.EnableTracing = false);
        services.AddMethodCacheOpenTelemetry();

        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<OpenTelemetryOptions>>().Value.EnableTracing.Should().BeFalse();
    }

    // Options bound with services.Configure before the call
    [Fact]
    public async Task AddMethodCacheOpenTelemetry_AfterConfigure_KeepsConfiguredOptions()
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.Configure<OpenTelemetryOptions>(o => o.RecordCacheKeys = true);
        services.AddMethodCacheOpenTelemetry();

        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<OpenTelemetryOptions>>().Value.RecordCacheKeys.Should().BeTrue();
    }

    // Every core writes a hit; with no listener it must not contend on a shared counter
    [Fact]
    public void RecordCacheHit_NoMetricsListener_WritesNoSharedCounter()
    {
        using var provider = new CacheMeterProvider(Options.Create(new OpenTelemetryOptions()));

        provider.RecordCacheHit("NoListener");

        var totalHits = (long)typeof(CacheMeterProvider)
            .GetField("_totalHits", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(provider)!;
        totalHits.Should().Be(0, "with no listener the hot path should not touch a shared Interlocked counter");
    }

    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentBag<(string Instrument, double Value, string? Method)> _measurements = new();

        public Recorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == MetricInstruments.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Add(i, v, t));
            _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Add(i, v, t));
            _listener.Start();
        }

        public IEnumerable<double> Values(string instrument, string method) =>
            _measurements.Where(m => m.Instrument == instrument && m.Method == method).Select(m => m.Value).ToList();

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
