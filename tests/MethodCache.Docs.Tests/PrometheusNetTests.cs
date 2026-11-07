using System.Diagnostics.Metrics;
using System.Text;
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;
using Xunit;

namespace MethodCache.Docs.Tests;

/// <summary>The monitoring guide's prometheus-net claims: the meter adapter exports MethodCache's instruments as gauges
/// that hold running totals, a registry of the application's own needs a metric factory for that registry as well, and
/// the guide's filter keeps other meters out.</summary>
public class PrometheusNetTests
{
    [Fact]
    public async Task CustomRegistry_WithItsMetricFactory_ExportsTheRequestCounter()
    {
        var registry = Metrics.NewCustomRegistry();
        using var adapter = global::Docs.Monitoring.Setup.PrometheusNetRegistry(registry);

        var text = await ScrapeAfterOneCallAsync(registry);

        Assert.Contains("methodcache_methodcache_requests", text);
    }

    [Fact]
    public async Task CustomRegistry_WithTheFilter_ExportsNoOtherMeter()
    {
        var registry = Metrics.NewCustomRegistry();
        using var adapter = global::Docs.Monitoring.Setup.PrometheusNetRegistry(registry);
        using var other = new Meter("Docs.OtherMeter");
        var orders = other.CreateCounter<long>("orders.placed");
        orders.Add(1);

        var text = await ScrapeAfterOneCallAsync(registry);
        orders.Add(1);
        text += await ScrapeAsync(registry);

        Assert.Contains("methodcache_methodcache_requests", text);
        Assert.DoesNotContain("orders_placed", text);
    }

    [Fact]
    public async Task RequestSeries_IsAGauge_ThatHoldsTheRunningTotal()
    {
        // The guide's PromQL takes rate() of it, which needs a value that only grows. Other tests in the process add
        // hits of their own, so the check is that two more hits raise the value by at least two.
        var registry = Metrics.NewCustomRegistry();
        using var adapter = global::Docs.Monitoring.Setup.PrometheusNetRegistry(registry);
        await using var provider = new ServiceCollection().AddMethodCache().BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheManager>();

        await ScrapeAsync(registry);
        for (var i = 0; i < 2; i++) await cache.GetOrCreateAsync("prometheus:total", _ => ValueTask.FromResult(42));
        var first = await ScrapeAsync(registry);
        for (var i = 0; i < 2; i++) await cache.GetOrCreateAsync("prometheus:total", _ => ValueTask.FromResult(42));
        var second = await ScrapeAsync(registry);

        Assert.Contains("# TYPE methodcache_methodcache_requests gauge", first.Split('\n'));
        Assert.True(ManualHits(first) >= 1);
        Assert.True(ManualHits(second) >= ManualHits(first) + 2, $"{ManualHits(first)} then {ManualHits(second)}");
    }

    private static double ManualHits(string scrape) => scrape.Split('\n')
        .Where(static line => line.StartsWith("methodcache_methodcache_requests{", StringComparison.Ordinal)
            && line.Contains("methodcache_kind=\"manual\"", StringComparison.Ordinal)
            && line.Contains("methodcache_result=\"hit\"", StringComparison.Ordinal))
        .Sum(static line => double.Parse(line[(line.LastIndexOf(' ') + 1)..], System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public async Task CustomRegistry_WithoutItsMetricFactory_ExportsNoMethodCacheSeries()
    {
        var registry = Metrics.NewCustomRegistry();
        using var adapter = MeterAdapter.StartListening(new MeterAdapterOptions { Registry = registry });

        var text = await ScrapeAfterOneCallAsync(registry);

        Assert.DoesNotContain("methodcache_methodcache_requests", text);
    }

    private static async Task<string> ScrapeAfterOneCallAsync(CollectorRegistry registry)
    {
        await using var provider = new ServiceCollection().AddMethodCache().BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheManager>();

        await ScrapeAsync(registry); // the adapter starts listening at the first scrape
        await cache.GetOrCreateAsync("prometheus:probe", _ => ValueTask.FromResult(42));
        return await ScrapeAsync(registry);
    }

    private static async Task<string> ScrapeAsync(CollectorRegistry registry)
    {
        using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
