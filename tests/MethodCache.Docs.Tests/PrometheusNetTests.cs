using System.Text;
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;
using Xunit;

namespace MethodCache.Docs.Tests;

/// <summary>The monitoring guide's prometheus-net claims: the meter adapter exports MethodCache's instruments, and a
/// registry of the application's own needs a metric factory for that registry as well.</summary>
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
