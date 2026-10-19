using MethodCache.Core.Telemetry;
using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Contracts;

public class MemoryStatisticsTests
{
    [Fact]
    public async Task DefaultWeight_DoesNotPretendToMeasurePayloadBytes()
    {
        using var cache = new InMemoryCacheManager(null, new FakeTimeProvider());
        await cache.SetAsync("key", new byte[1], TimeSpan.FromMinutes(1));
        Assert.Equal(1, (await cache.GetStatsAsync()).TotalWeight);
        await cache.SetAsync("key", new byte[1024 * 1024], TimeSpan.FromMinutes(1));
        var stats = await cache.GetStatsAsync();
        Assert.Equal(1, stats.TotalWeight);
        Assert.Equal(1, stats.LocalMethodEntries);
    }

    [Fact]
    public async Task ConfiguredWeight_UsesApplicationUnits()
    {
        using var cache = new InMemoryCacheManager(Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions { Weigher = (_, value) => ((byte[])value!).Length }));
        await cache.SetAsync("key", new byte[4096], TimeSpan.FromMinutes(1));
        var stats = await cache.GetStatsAsync();
        Assert.Equal(4096, stats.TotalWeight);
        Assert.Equal(1, stats.LocalMethodEntries);
    }

    [Fact]
    public async Task Statistics_DoNotSerializeOrExecutePayloadGetters()
    {
        using var cache = new InMemoryCacheManager();
        await cache.SetAsync("key", new HostileGetter(), TimeSpan.FromMinutes(1));
        for (var i = 0; i < 10; i++) Assert.Equal(1, (await cache.GetStatsAsync()).TotalWeight);
    }

    [Fact]
    public async Task LocalAndObservedHttpScopes_AreNotAddedToMethodWeightOrResidentEntries()
    {
        using var provider = new ServiceCollection().AddMethodCache().BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheManager>();
        await cache.GetOrCreateAsync("method", _ => ValueTask.FromResult(1));
        provider.GetRequiredService<CoreTelemetry>().ObserveHttp(() => new CacheObservation(0, 2, 128, 100, 0));
        var stats = await provider.GetRequiredService<ICacheAdministrator>().GetStatsAsync();
        Assert.Equal(1, stats.LocalMethodEntries);
        Assert.Equal(1, stats.TotalWeight);
        Assert.Equal(2, stats.LocalHttpEntries);
        Assert.Equal(128, stats.LocalHttpBytes);
        Assert.Equal(100, stats.ObservedDistributedEntries);
    }

    private sealed class HostileGetter
    {
        public int Value => throw new InvalidOperationException("Statistics must not run application getters.");
    }
}
