using System.Diagnostics;
using System.Diagnostics.Metrics;
using MethodCache.Core.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Telemetry;

/// <summary>
/// The attribute names, values and histogram buckets dashboards are built on. They are frozen at 1.0.
/// </summary>
public class TelemetryNamingTests
{
    // The bucket boundaries ASP.NET Core advises for http.server.request.duration, in seconds
    private static readonly double[] SecondBuckets = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10];

    [Fact]
    public async Task GeneratedCall_ReportsKindMethodAndResult_UnderTheMethodCacheNamespace()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITelemetryShapes, TelemetryShapes>();
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();
        using var recorded = new TelemetryRecorder(provider);
        var shapes = provider.GetRequiredService<ITelemetryShapes>();

        await shapes.ReadAsync(1);
        await shapes.ReadAsync(1);

        var requests = recorded.Longs.Where(row => row.Name == "methodcache.requests").ToArray();
        Assert.Equal(2, requests.Length);
        Assert.All(requests, row =>
        {
            Assert.Equal(new[] { "methodcache.kind", "methodcache.method", "methodcache.result" }, row.Tags.Keys.Order(StringComparer.Ordinal));
            Assert.Equal("method", row.Tags["methodcache.kind"]);
            Assert.Contains("ITelemetryShapes.ReadAsync", (string)row.Tags["methodcache.method"]!);
        });
        Assert.Equal(new[] { "hit", "miss" }, requests.Select(row => (string)row.Tags["methodcache.result"]!).Order(StringComparer.Ordinal));
        var load = Assert.Single(recorded.Loads);
        Assert.Equal(new[] { "methodcache.kind", "methodcache.method" }, load.Tags.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("method", load.Tags["methodcache.kind"]);
    }

    [Fact]
    public async Task ManualCall_ReportsKindManual_WithoutAMethodName()
    {
        using var provider = new ServiceCollection().AddMethodCache().BuildServiceProvider();
        using var recorded = new TelemetryRecorder(provider);
        var cache = provider.GetRequiredService<ICacheManager>();

        await cache.GetOrCreateAsync("key", _ => ValueTask.FromResult(7));
        await cache.GetOrCreateAsync<int>("key", _ => throw new InvalidOperationException("must hit"));
        await cache.TryGetAsync<int>("key");

        var requests = recorded.Longs.Where(row => row.Name == "methodcache.requests").ToArray();
        Assert.Equal(3, requests.Length);
        Assert.All(requests, row =>
        {
            Assert.Equal(new[] { "methodcache.kind", "methodcache.result" }, row.Tags.Keys.Order(StringComparer.Ordinal));
            Assert.Equal("manual", row.Tags["methodcache.kind"]);
        });
        var load = Assert.Single(recorded.Loads);
        Assert.Equal("manual", Assert.Single(load.Tags).Value);
        Assert.DoesNotContain(recorded.Longs.SelectMany(row => row.Tags.Values).Concat(load.Tags.Values),
            value => value is string text && text.StartsWith("MethodCache.Fluent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BulkCall_ReportsKindManual_MarkedAsBulk()
    {
        var clock = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();
        using var recorded = new TelemetryRecorder(provider);

        await provider.GetRequiredService<ICacheManager>().GetOrCreateManyAsync<int>(["a", "b"],
            (keys, _) => ValueTask.FromResult<IDictionary<string, int>>(keys.ToDictionary(key => key, _ => 7)));

        var bulk = Assert.Single(recorded.Longs, row => row.Name == "methodcache.requests" && row.Tags.ContainsKey("methodcache.bulk"));
        Assert.Equal(new[] { "methodcache.bulk", "methodcache.kind", "methodcache.result" }, bulk.Tags.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("manual", bulk.Tags["methodcache.kind"]);
        Assert.Equal(true, bulk.Tags["methodcache.bulk"]);
        Assert.Equal("miss", bulk.Tags["methodcache.result"]);
        var load = Assert.Single(recorded.Loads);
        Assert.Equal("manual", load.Tags["methodcache.kind"]);
        Assert.Equal(true, load.Tags["methodcache.bulk"]);
    }

    [Fact]
    public async Task Invalidations_ReportNamespacedTypeAndStage()
    {
        using var provider = new ServiceCollection().AddMethodCache().BuildServiceProvider();
        using var recorded = new TelemetryRecorder(provider);
        var cache = provider.GetRequiredService<ICacheManager>();

        await cache.InvalidateKeysAsync(["a"]);
        await cache.InvalidateTagsAsync(["t"]);
        await provider.GetRequiredService<ICacheAdministrator>().ClearAsync();

        var invalidations = recorded.Longs.Where(row => row.Name == "methodcache.invalidations").ToArray();
        Assert.All(invalidations, row => Assert.Equal(new[] { "methodcache.invalidation.stage", "methodcache.invalidation.type" },
            row.Tags.Keys.Order(StringComparer.Ordinal)));
        Assert.Equal(new[] { "clear", "key", "tag" }, invalidations.Select(row => (string)row.Tags["methodcache.invalidation.type"]!).Distinct().Order(StringComparer.Ordinal));
        Assert.All(invalidations, row => Assert.Equal("local", row.Tags["methodcache.invalidation.stage"]));
    }

    [Fact]
    public void ObservedGauges_ReportNamespacedKindAndStage()
    {
        using var provider = new ServiceCollection().AddMethodCache().BuildServiceProvider();
        using var recorded = new TelemetryRecorder(provider);
        _ = provider.GetRequiredService<CoreTelemetry>();

        recorded.Poll();

        var entries = recorded.Longs.Where(row => row.Name == "methodcache.entries").ToArray();
        Assert.Equal(new[] { "http", "method" }, entries.Select(row => (string)Assert.Single(row.Tags, tag => tag.Key == "methodcache.kind").Value!).Order(StringComparer.Ordinal));
        var queues = recorded.Longs.Where(row => row.Name == "methodcache.distributed.queue").ToArray();
        Assert.Equal(new[] { "publication", "storage" }, queues.Select(row => (string)Assert.Single(row.Tags, tag => tag.Key == "methodcache.invalidation.stage").Value!).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("methodcache.load.duration")]
    [InlineData("methodcache.distributed.duration")]
    public void DurationHistograms_AdviseSecondScaleBuckets(string name)
    {
        using var provider = new ServiceCollection().AddMethodCache().BuildServiceProvider();
        var factory = provider.GetRequiredService<IMeterFactory>();
        Histogram<double>? histogram = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Name == name) histogram = (Histogram<double>)instrument;
            }
        };
        _ = provider.GetRequiredService<CoreTelemetry>();
        listener.Start();

        Assert.NotNull(histogram);
        Assert.Equal("s", histogram.Unit);
        Assert.Equal(SecondBuckets, histogram.Advice?.HistogramBucketBoundaries);
    }

    [Fact]
    public async Task Spans_UseNamespacedAttributes_AndTheDistributedSpanIsInternal()
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.AddMethodCacheDistributedCache(_ => new MethodCache.TestSupport.AtomicTestCache());
        using var provider = services.BuildServiceProvider();
        var telemetry = provider.GetRequiredService<CoreTelemetry>();
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, telemetry.Source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (spans) spans.Add(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        var cache = provider.GetRequiredService<ICacheManager>();

        await cache.GetOrCreateAsync("key", _ => ValueTask.FromResult(7));
        await cache.InvalidateTagsAsync(["t"]);

        Activity[] recorded;
        lock (spans) recorded = spans.ToArray();
        var load = Assert.Single(recorded, span => span.OperationName == "MethodCache.load");
        Assert.Equal(ActivityKind.Internal, load.Kind);
        Assert.Equal("manual", load.GetTagItem("methodcache.kind"));
        var distributed = recorded.Where(span => span.OperationName == "MethodCache.distributed").ToArray();
        Assert.NotEmpty(distributed);
        Assert.All(distributed, span =>
        {
            Assert.Equal(ActivityKind.Internal, span.Kind);
            Assert.NotNull(span.GetTagItem("methodcache.operation"));
        });
        var invalidation = Assert.Single(recorded, span => span.OperationName == "MethodCache.invalidate");
        Assert.Equal("tag", invalidation.GetTagItem("methodcache.invalidation.type"));
        Assert.All(recorded, span => Assert.All(span.TagObjects, tag =>
            Assert.True(tag.Key.StartsWith("methodcache.", StringComparison.Ordinal) || tag.Key == "error.type", tag.Key)));
    }

    [Fact]
    public void HttpRequests_ReportKindHttpAndTheClientName()
    {
        using var provider = new ServiceCollection().AddMethodCache().BuildServiceProvider();
        using var recorded = new TelemetryRecorder(provider);
        var telemetry = provider.GetRequiredService<CoreTelemetry>();

        telemetry.Request("http", "orders-api", RequestResult.Revalidated);
        using (telemetry.BeginLoad("http", "orders-api")) { }

        var request = Assert.Single(recorded.Longs, row => row.Name == "methodcache.requests");
        Assert.Equal("http", request.Tags["methodcache.kind"]);
        Assert.Equal("orders-api", request.Tags["methodcache.http.client"]);
        Assert.Equal("revalidated", request.Tags["methodcache.result"]);
        Assert.False(request.Tags.ContainsKey("methodcache.method"));
        var load = Assert.Single(recorded.Loads);
        Assert.Equal("orders-api", load.Tags["methodcache.http.client"]);
    }
}
