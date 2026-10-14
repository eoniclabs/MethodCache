using MethodCache.Extensibility;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Contracts;

public class MemoryStatisticsTests
{
    [Fact]
    public async Task DefaultMemoryEstimate_TracksPayloadGrowth()
    {
        using var cache = new InMemoryCacheManager(new NullCacheMetricsProvider(), null, new FakeTimeProvider());
        await cache.SetAsync("key", new byte[1], TimeSpan.FromMinutes(1));
        var small = (await cache.GetStatsAsync()).MemoryUsage;
        await cache.SetAsync("key", new byte[1024 * 1024], TimeSpan.FromMinutes(1));
        var large = (await cache.GetStatsAsync()).MemoryUsage;
        Assert.True(large - small >= 1024 * 1024 - 1);
    }
}
