using MethodCache.Extensibility;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using MethodCache.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace MethodCache.OpenTelemetry.Tests;

// SDK subscriptions are process-wide. Serialize these tests so exporter totals and allocation probes
// cannot accidentally observe another test container; Core's separate tests verify factory isolation.
[CollectionDefinition("Core SDK instrumentation", DisableParallelization = true)]
public class CoreSdkCollection;

[Collection("Core SDK instrumentation")]
public class CoreInstrumentationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SdkRegistration_EitherOrderAndRepeated_DoesNotWrapOrReplaceManager(bool sdkFirst)
    {
        var services = new ServiceCollection();
        var exported = new List<Activity>();
        using var sdk = sdkFirst ? Sdk.CreateTracerProviderBuilder().AddMethodCacheInstrumentation()
            .AddMethodCacheInstrumentation().AddInMemoryExporter(exported).Build() : null;
        services.AddMethodCache();
        using var after = sdkFirst ? null : Sdk.CreateTracerProviderBuilder().AddMethodCacheInstrumentation()
            .AddMethodCacheInstrumentation().AddInMemoryExporter(exported).Build();
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<ICacheManager>();
        Assert.IsType<InMemoryCacheManager>(manager);
        await manager.GetOrCreateAsync("key", _ => ValueTask.FromResult(7));
        await manager.GetOrCreateAsync<int>("key", _ => throw new Exception("must hit"));
        Assert.Equal("MethodCache.load", Assert.Single(exported).OperationName);
        var stats = await provider.GetRequiredService<ICacheAdministrator>().GetStatsAsync();
        Assert.Equal(1, stats.LocalMethodEntries);
        Assert.Equal(1, stats.Hits);
        Assert.Equal(1, stats.Misses);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9999)]
    [InlineData(19000)]
    public async Task Exporter_RecordsFractionalSeconds_WithoutTruncation(long ticks)
    {
        var clock = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();
        var exported = new List<Metric>();
        using var sdk = Sdk.CreateMeterProviderBuilder().AddMethodCacheInstrumentation().AddInMemoryExporter(exported).Build();
        await provider.GetRequiredService<ICacheManager>().GetOrCreateAsync("key", _ =>
        {
            clock.Advance(TimeSpan.FromTicks(ticks));
            return ValueTask.FromResult(7);
        });
        Assert.True(sdk.ForceFlush());
        var duration = Assert.Single(exported, metric => metric.Name == "methodcache.load.duration");
        Assert.Equal("s", duration.Unit);
        var count = 0;
        Inspect(duration, point =>
        {
            Assert.Equal(1, point.GetHistogramCount());
            Assert.Equal(TimeSpan.FromTicks(ticks).TotalSeconds, point.GetHistogramSum(), 12);
            count++;
        });
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Exporter_BucketsLoadDurationsInSeconds()
    {
        // Without advice the SDK uses 0, 5, 10 ... 10000, made for milliseconds, and every load lands in the first bucket
        var clock = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();
        var exported = new List<Metric>();
        using var sdk = Sdk.CreateMeterProviderBuilder().AddMethodCacheInstrumentation().AddInMemoryExporter(exported).Build();

        await provider.GetRequiredService<ICacheManager>().GetOrCreateAsync("key", _ =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(30));
            return ValueTask.FromResult(7);
        });

        Assert.True(sdk.ForceFlush());
        var duration = Assert.Single(exported, metric => metric.Name == "methodcache.load.duration");
        var bounds = new List<double>();
        double? recordedIn = null;
        Inspect(duration, point =>
        {
            foreach (var bucket in point.GetHistogramBuckets())
            {
                bounds.Add(bucket.ExplicitBound);
                if (bucket.BucketCount == 1) recordedIn = bucket.ExplicitBound;
            }
        });
        Assert.Equal(new[] { 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10, double.PositiveInfinity }, bounds);
        Assert.Equal(0.05, recordedIn);
    }

    [Fact]
    public async Task Exporter_ReceivesExactlyOneOutcomePerCaller_NoSpansForPureHits()
    {
        var exported = new List<Metric>();
        var spans = new List<Activity>();
        using var meter = Sdk.CreateMeterProviderBuilder().AddMethodCacheInstrumentation().AddInMemoryExporter(exported).Build();
        using var tracer = Sdk.CreateTracerProviderBuilder().AddMethodCacheInstrumentation().AddInMemoryExporter(spans).Build();
        using var provider = new ServiceCollection().AddMethodCache().BuildServiceProvider();
        var manager = provider.GetRequiredService<ICacheManager>();
        await manager.GetOrCreateAsync("key", _ => ValueTask.FromResult(7));
        for (var i = 0; i < 100; i++) await manager.GetOrCreateAsync<int>("key", _ => throw new Exception("hit"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await manager.GetOrCreateAsync<int>("failed", _ => throw new InvalidOperationException("secret-error")));
        Assert.True(meter.ForceFlush());
        var metric = Assert.Single(exported, metric => metric.Name == "methodcache.requests");
        var outcomes = new Dictionary<string, long>();
        Inspect(metric, point =>
        {
            string? result = null;
            foreach (var tag in point.Tags) if (tag.Key == "methodcache.result") result = (string?)tag.Value;
            outcomes[result!] = point.GetSumLong();
        });
        Assert.Equal(100, outcomes["hit"]);
        Assert.Equal(1, outcomes["miss"]);
        Assert.Equal(1, outcomes["error"]);
        Assert.Equal(2, spans.Count);
        var error = Assert.Single(spans, span => span.Status == ActivityStatusCode.Error);
        Assert.Equal(typeof(InvalidOperationException).FullName, error.GetTagItem("error.type"));
        Assert.Null(error.StatusDescription);
        Assert.Empty(error.Events);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task Exporter_IdentitiesRequireExplicitCoreOptions_AndNeverBecomeMetricDimensions(bool keys, bool hash, bool tags)
    {
        const string canary = "https://private.test/user?token=canary-tenant";
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.Configure<MethodCacheTelemetryOptions>(o => { o.RecordCacheKeys = keys; o.HashCacheKeys = hash; o.RecordCacheTags = tags; });
        using var provider = services.BuildServiceProvider();
        var exported = new List<Metric>();
        var spans = new List<Activity>();
        using var meter = Sdk.CreateMeterProviderBuilder().AddMethodCacheInstrumentation().AddInMemoryExporter(exported).Build();
        using var tracer = Sdk.CreateTracerProviderBuilder().AddMethodCacheInstrumentation().AddInMemoryExporter(spans).Build();
        using var request = new Activity("request").AddTag("http.url", canary).Start();
        request.ActivityTraceFlags = ActivityTraceFlags.Recorded;
        var manager = provider.GetRequiredService<ICacheManager>();
        await manager.GetOrCreateAsync(canary, _ => ValueTask.FromResult(7), b => b.AddTags(canary));
        await manager.InvalidateKeysAsync([canary]);
        await manager.InvalidateTagsAsync([canary]);
        Assert.Equal(3, spans.Count);
        foreach (var span in spans)
        {
            Assert.Equal(request.TraceId, span.TraceId);
            Assert.Null(span.GetTagItem("http.url"));
            Assert.Null(span.GetTagItem("user.id"));
            Assert.Null(span.GetTagItem("tenant.id"));
            if (!tags && (!keys || hash)) Assert.DoesNotContain(canary, string.Join(";", span.TagObjects.SelectMany(pair => Values(pair.Value))));
        }
        var load = Assert.Single(spans, span => span.OperationName == "MethodCache.load");
        Assert.Equal(keys, load.GetTagItem("methodcache.key") != null);
        if (keys) Assert.Equal(hash ? 64 : canary.Length, ((string)load.GetTagItem("methodcache.key")!).Length);
        Assert.Equal(tags, load.GetTagItem("methodcache.tags") != null);
        Assert.True(meter.ForceFlush());
        foreach (var metric in exported)
            Inspect(metric, point =>
            {
                foreach (var tag in point.Tags) Assert.DoesNotContain(canary, tag.Value?.ToString() ?? "");
            });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NullableLookup_IsAHitEvenWithFastPathDisabledOrRefreshDue(bool fastPath, bool refreshDue)
    {
        var clock = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddMethodCache();
        services.Configure<MethodCacheMemoryOptions>(o => o.EnableFastPath = fastPath);
        using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<ICacheManager>();
        await manager.GetOrCreateAsync<string?>("null", _ => ValueTask.FromResult<string?>(null), b => b.WithDuration(TimeSpan.FromSeconds(10)).WithRefreshAhead(TimeSpan.FromSeconds(2)));
        await manager.GetOrCreateAsync<int?>("nullable", _ => ValueTask.FromResult<int?>(null));
        if (refreshDue) clock.Advance(TimeSpan.FromSeconds(9));
        var exported = new List<Metric>();
        using var sdk = Sdk.CreateMeterProviderBuilder().AddMethodCacheInstrumentation().AddInMemoryExporter(exported).Build();
        var reference = await manager.TryGetAsync<string?>("null");
        var nullable = await manager.TryGetAsync<int?>("nullable");
        Assert.True(reference.Found);
        Assert.Null(reference.Value);
        Assert.True(nullable.Found);
        Assert.Null(nullable.Value);
        Assert.False((await manager.TryGetAsync<int?>("absent")).Found);
        Assert.True(sdk.ForceFlush());
        long hits = 0, misses = 0;
        foreach (var metric in exported.Where(m => m.Name == "methodcache.requests"))
            Inspect(metric, point =>
            {
                foreach (var tag in point.Tags)
                    if (tag.Key == "methodcache.result") { if (Equals(tag.Value, "hit")) hits += point.GetSumLong(); else if (Equals(tag.Value, "miss")) misses += point.GetSumLong(); }
            });
        Assert.Equal(2, hits);
        Assert.Equal(1, misses);
    }

    [Fact]
    public async Task LateSubscriber_ObservesCurrentLocalEntriesAndWeight()
    {
        using var provider = new ServiceCollection().AddMethodCache().BuildServiceProvider();
        await provider.GetRequiredService<ICacheManager>().GetOrCreateAsync("key", _ => ValueTask.FromResult(new byte[100_000]));
        var exported = new List<Metric>();
        using var sdk = Sdk.CreateMeterProviderBuilder().AddMethodCacheInstrumentation().AddInMemoryExporter(exported).Build();
        Assert.True(sdk.ForceFlush());
        var metric = Assert.Single(exported, m => m.Name == "methodcache.entries");
        long methods = -1;
        Inspect(metric, point =>
        {
            foreach (var tag in point.Tags) if (tag.Key == "methodcache.kind" && Equals(tag.Value, "method")) methods = point.GetSumLong();
        });
        Assert.Equal(1, methods);
        var stats = await provider.GetRequiredService<ICacheAdministrator>().GetStatsAsync();
        Assert.Equal(1, stats.TotalWeight);
        Assert.Equal(0, stats.LocalHttpBytes);
    }

    private static void Inspect(Metric metric, Action<MetricPoint> inspect)
    {
        foreach (ref readonly var point in metric.GetMetricPoints()) inspect(point);
    }

    private static IEnumerable<string> Values(object? value) => value is string text ? [text] : value is string[] texts ? texts : [];
}
