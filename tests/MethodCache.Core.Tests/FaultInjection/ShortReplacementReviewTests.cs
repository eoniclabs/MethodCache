using MethodCache.Core.Runtime.Distributed;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.FaultInjection;

public class ShortReplacementReviewTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9999)]
    public async Task Set_WithSubMillisecondLifetime_RemovesThePreviousDistributedValue(long ticks)
    {
        var time = new FakeTimeProvider();
        var backend = new MethodCache.TestSupport.AtomicTestCache();
        DistributedCacheLayer Layer() => new(backend, new JsonCacheSerializer(), new DistributedCacheOptions { ClockSkewTolerance = TimeSpan.Zero, TagCheckInterval = TimeSpan.Zero }, timeProvider: time);
        using var firstLayer = Layer();
        using var first = new InMemoryCacheManager(new NullCacheMetricsProvider(), Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }), time, firstLayer);
        await first.SetAsync("key", "old", TimeSpan.FromMinutes(10));
        await first.SetAsync("key", "replacement", TimeSpan.FromTicks(ticks));
        using var secondLayer = Layer();
        using var second = new InMemoryCacheManager(new NullCacheMetricsProvider(), Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }), time, secondLayer);
        var result = await second.GetOrCreateFastAsync("key", "M", () => Task.FromResult("fresh"), CacheRuntimePolicy.Empty("M") with { Duration = TimeSpan.FromMinutes(1) });
        Assert.Equal("fresh", result);
    }
}
