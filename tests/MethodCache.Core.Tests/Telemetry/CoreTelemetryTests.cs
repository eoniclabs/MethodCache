using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using MethodCache.Core.Telemetry;
using NSubstitute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Telemetry;

public class CoreTelemetryTests
{
    private static object? _allocationProbeSink;
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
        Assert.Contains(measurements, row => ReferenceEquals(row.Scope, aFactory) && row.Name == "methodcache.entries" && row.Value == 7 && Equals(row.Tags["methodcache.kind"], "method"));
        Assert.Contains(measurements, row => ReferenceEquals(row.Scope, bFactory) && row.Name == "methodcache.entries" && row.Value == 11);
        Assert.Contains(measurements, row => ReferenceEquals(row.Scope, aFactory) && row.Name == "methodcache.entries" && row.Value == 3 && Equals(row.Tags["methodcache.kind"], "http"));
        Assert.Contains(measurements, row => ReferenceEquals(row.Scope, aFactory) && row.Name == "methodcache.http.size" && row.Value == 4096);
        Assert.Equal(2, measurements.Count(row => row.Name == "methodcache.requests"));
    }

    [Fact]
    public async Task DecoratedBuiltInManager_EmitsOneRequestAndOneOriginLoad()
    {
        using var provider = new ServiceCollection().AddMethodCache().BuildServiceProvider();
        var factory = provider.GetRequiredService<IMeterFactory>();
        var requests = 0L;
        var loads = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, self) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory)) self.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        { if (instrument.Name == "methodcache.requests") requests += value; });
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) =>
        { if (instrument.Name == "methodcache.load.duration") loads++; });
        listener.Start();
        var telemetry = provider.GetRequiredService<CoreTelemetry>();
        var wrapper = new ForwardingManager(provider.GetRequiredService<ICacheManager>());
        var policy = MethodCache.Infrastructure.CacheRuntimePolicy.Empty("wrapped");
        Assert.Equal(42, await telemetry.CustomAsync(wrapper, "key", policy, () => Task.FromResult(42), default));
        Assert.Equal(42, await telemetry.CustomAsync(wrapper, "key", policy, () => Task.FromResult(0), default));
        Assert.Equal(2, requests);
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task FailedWriteRepair_DoesNotCountAsAnApplicationInvalidation()
    {
        using var provider = new ServiceCollection().AddMethodCache().BuildServiceProvider();
        var factory = provider.GetRequiredService<IMeterFactory>();
        var invalidations = new List<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, self) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Name == "methodcache.invalidations") self.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        { if (value != 0) invalidations.Add(tags.ToArray().Single(tag => tag.Key == "methodcache.invalidation.stage").Value!.ToString()!); });
        listener.Start();
        var backend = NSubstitute.Substitute.For<MethodCache.Extensibility.IAtomicInvalidationStore>();
        backend.SetAsync(NSubstitute.Arg.Any<string>(), NSubstitute.Arg.Any<byte[]>(), NSubstitute.Arg.Any<Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions>(), NSubstitute.Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("offline")));
        await using var layer = new MethodCache.Core.Runtime.Distributed.DistributedCacheLayer(backend,
            new MethodCache.Extensibility.JsonCacheSerializer(), new DistributedCacheOptions(), telemetry: provider.GetRequiredService<CoreTelemetry>());
        await layer.WriteAsync("write", 42, null, layer.Now, TimeSpan.FromMinutes(1));
        Assert.Equal(1, layer.GetInvalidationQueues().Distributed);
        await layer.FlushAsync(force: true);
        Assert.Equal(0, layer.GetInvalidationQueues().Distributed);
        Assert.Empty(invalidations);
    }

    private sealed class ForwardingManager(ICacheManager inner) : ICacheManager
    {
        private readonly MethodCache.NoOpCacheManager _noOp = new();
        public ValueTask<T> GetOrCreateAsync<TState, T>(string key, TState state, Func<TState, CancellationToken, ValueTask<T>> factory,
            CacheEntryOptions? options = null, CancellationToken token = default) => inner.GetOrCreateAsync(key, state, factory, options, token);
        public ValueTask<CacheLookupResult<T>> TryGetAsync<T>(string key, int? version = null, CancellationToken cancellationToken = default)
            => _noOp.TryGetAsync<T>(key, version, cancellationToken);
        public ValueTask<InvalidationResult> InvalidateKeysAsync(IEnumerable<string> keys, int? version = null, CancellationToken cancellationToken = default)
            => _noOp.InvalidateKeysAsync(keys, version, cancellationToken);
        public ValueTask<InvalidationResult> InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
            => _noOp.InvalidateTagsAsync(tags, cancellationToken);
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
    public async Task NoListeners_RequestAndOperationChecksAllocateNothing()
    {
        Assert.Equal(0, await MeasureNoListenerOperationsOnOwnedThread());
    }

    [Fact]
    public async Task NoListenerProbe_DetectsEvenOneAllocationInsideItsMeasuredWindow()
    {
        try
        {
            var allocated = await MeasureNoListenerOperationsOnOwnedThread(static () => _allocationProbeSink = new object());
            Assert.True(allocated > 0);
            Assert.NotNull(_allocationProbeSink);
        }
        finally { _allocationProbeSink = null; }
    }

    private static Task<long> MeasureNoListenerOperationsOnOwnedThread(Action? insideMeasuredWindow = null)
    {
        var completion = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ExecutionContext.SuppressFlow())
        {
            new Thread(() =>
            {
                try
                {
                    using var provider = new ServiceCollection().AddMethodCache().BuildServiceProvider();
                    var telemetry = provider.GetRequiredService<CoreTelemetry>();
                    for (var i = 0; i < 1000; i++)
                    {
                        telemetry.Request("method", "M", RequestResult.Hit);
                        EnsureNoOperation(telemetry);
                    }
                    // Warm the complete probe, including its allocation counters and loop/JIT path, once.
                    // The following single fixed window still requires exactly zero bytes; no retry/minimum.
                    _ = MeasureNoListenerOperations(telemetry);
                    completion.SetResult(MeasureNoListenerOperations(telemetry, insideMeasuredWindow));
                }
                catch (Exception error) { completion.SetException(error); }
            })
            { IsBackground = true }.Start();
        }
        return completion.Task;
    }

    // Count only the actual telemetry calls; assertions, setup and completion stay outside.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureNoListenerOperations(CoreTelemetry telemetry, Action? insideMeasuredWindow = null)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        insideMeasuredWindow?.Invoke();
        for (var i = 0; i < 10_000; i++)
        {
            telemetry.Request("method", "M", RequestResult.Hit);
            EnsureNoOperation(telemetry);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void EnsureNoOperation(CoreTelemetry telemetry)
    {
        var operation = telemetry.BeginLoad("method", "M");
        if (operation == null) return;
        operation.Dispose();
        throw new InvalidOperationException("A telemetry scope requires a listener.");
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
