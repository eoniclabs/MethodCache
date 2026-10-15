using System.Diagnostics;
using System.Diagnostics.Metrics;
using MethodCache.Core.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Telemetry;

public class CoreTelemetryTests
{
    [Fact]
    public void Containers_UseTheirOwnMeterFactoriesAndObservationState()
    {
        using var first = new ServiceCollection().AddMethodCache().BuildServiceProvider();
        using var second = new ServiceCollection().AddMethodCache().BuildServiceProvider();
        var aFactory = first.GetRequiredService<IMeterFactory>();
        var bFactory = second.GetRequiredService<IMeterFactory>();
        Assert.NotSame(aFactory, bFactory);
        var measurements = new List<(object? Scope, string Name, long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, self) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, aFactory) || ReferenceEquals(instrument.Meter.Scope, bFactory))
                    self.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Meter.Scope, instrument.Name, value, tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value))));
        listener.Start();
        var a = first.GetRequiredService<CoreTelemetry>();
        var b = second.GetRequiredService<CoreTelemetry>();
        a.ObserveMethods(() => new CacheObservation(7, 0, 0, 0, 700));
        b.ObserveMethods(() => new CacheObservation(11, 0, 0, 0, 1100));
        a.ObserveHttp(() => new CacheObservation(0, 3, 4096, 100, 0));
        a.Request("method", "M", RequestResult.Hit);
        b.Request("method", "M", RequestResult.Miss);
        listener.RecordObservableInstruments();
        Assert.Contains(measurements, row => ReferenceEquals(row.Scope, aFactory) && row.Name == "methodcache.entries" && row.Value == 7 && Equals(row.Tags["kind"], "method"));
        Assert.Contains(measurements, row => ReferenceEquals(row.Scope, bFactory) && row.Name == "methodcache.entries" && row.Value == 11);
        Assert.Contains(measurements, row => ReferenceEquals(row.Scope, aFactory) && row.Name == "methodcache.entries" && row.Value == 3 && Equals(row.Tags["kind"], "http"));
        Assert.Contains(measurements, row => ReferenceEquals(row.Scope, aFactory) && row.Name == "methodcache.http.size" && row.Value == 4096);
        Assert.Equal(2, measurements.Count(row => row.Name == "methodcache.requests"));
    }

    [Fact]
    public void Instrumentation_UsesAnExistingFactory()
    {
        using var existing = new RecordingFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IMeterFactory>(existing);
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();
        Assert.Same(existing, provider.GetRequiredService<IMeterFactory>());
        _ = provider.GetRequiredService<CoreTelemetry>();
        Assert.Equal(MethodCacheTelemetry.Name, Assert.Single(existing.Names));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OriginScope_RecordsSecondsAndOnlyExplicitIdentityOptIns(bool recordKeys, bool recordTags)
    {
        const string canary = "secret-user?token=canary";
        var clock = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddMethodCache();
        services.Configure<MethodCacheTelemetryOptions>(options => { options.RecordCacheKeys = recordKeys; options.RecordCacheTags = recordTags; });
        using var provider = services.BuildServiceProvider();
        var telemetry = provider.GetRequiredService<CoreTelemetry>();
        var activities = new List<Activity>();
        using var activitiesListener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, telemetry.Source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(activitiesListener);
        var durations = new List<(double Value, Dictionary<string, object?> Tags)>();
        using var metrics = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == "methodcache.load.duration" && ReferenceEquals(instrument.Meter.Scope, provider.GetRequiredService<IMeterFactory>()))
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        metrics.SetMeasurementEventCallback<double>((_, value, tags, _) => durations.Add((value, tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value))));
        metrics.Start();
        using (var load = telemetry.BeginLoad("method", "M", canary, [canary]))
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            load!.Fail(new InvalidOperationException(canary));
        }
        Assert.Equal(2d, Assert.Single(durations).Value);
        Assert.Equal(typeof(InvalidOperationException).FullName, durations[0].Tags["error.type"]);
        Assert.DoesNotContain(durations[0].Tags.Values, value => value?.ToString()?.Contains(canary, StringComparison.Ordinal) == true);
        var activity = Assert.Single(activities);
        Assert.Equal(recordKeys, activity.GetTagItem("methodcache.key") != null);
        Assert.NotEqual(canary, activity.GetTagItem("methodcache.key"));
        Assert.Equal(recordTags, activity.GetTagItem("methodcache.tags") != null);
        Assert.Null(activity.GetTagItem("exception.message"));
        Assert.Null(activity.StatusDescription);
    }

    [Fact]
    public void NoListeners_RequestAndOperationChecksAllocateNothing()
    {
        using var provider = new ServiceCollection().AddMethodCache().BuildServiceProvider();
        var telemetry = provider.GetRequiredService<CoreTelemetry>();
        telemetry.Request("method", "M", RequestResult.Hit);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            telemetry.Request("method", "M", RequestResult.Hit);
            Assert.Null(telemetry.BeginLoad("method", "M"));
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class RecordingFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];
        internal List<string> Names { get; } = [];
        public Meter Create(MeterOptions options)
        {
            Names.Add(options.Name);
            var meter = new Meter(options.Name, options.Version, options.Tags, this);
            _meters.Add(meter);
            return meter;
        }
        public void Dispose() { foreach (var meter in _meters) meter.Dispose(); }
    }
}
