using MethodCache.Extensibility;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Contracts;

public class SamplingStatisticsTests
{
    [Fact]
    public async Task SamplingStatistics_BoundsSerialization_AndReusesTheSample()
    {
        var counter = new Counter();
        using var cache = new InMemoryCacheManager(new NullCacheMetricsProvider(), Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions
        {
            MemoryCalculationMode = MemoryUsageCalculationMode.Sampling,
            SamplingPercentage = 1,
            SamplingMaxEntries = 4,
            SamplingRecalculationInterval = 2
        }), new FakeTimeProvider());
        for (var i = 0; i < 100; i++)
        {
            await cache.SetAsync("key:" + i, new Payload(counter), TimeSpan.FromMinutes(1));
        }
        await cache.GetStatsAsync();
        Assert.Equal(4, counter.Reads);
        await cache.GetStatsAsync();
        Assert.Equal(4, counter.Reads);
        await cache.GetStatsAsync();
        Assert.Equal(8, counter.Reads);
    }

    private sealed class Counter { public int Reads; }
    private sealed class Payload(Counter counter)
    {
        public int Value { get { Interlocked.Increment(ref counter.Reads); return 42; } }
    }
}
