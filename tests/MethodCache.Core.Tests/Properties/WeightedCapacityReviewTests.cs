using CsCheck;
using MethodCache.Extensibility;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

public class WeightedCapacityReviewTests
{
    [Theory]
    [InlineData(MemoryCacheEvictionPolicy.TinyLfu)]
    [InlineData(MemoryCacheEvictionPolicy.Lru)]
    [InlineData(MemoryCacheEvictionPolicy.Fifo)]
    public void TotalWeight_CannotOverflowAndBypassCapacity(MemoryCacheEvictionPolicy eviction)
    {
        Gen.OneOfConst(long.MaxValue / 2 + 1, long.MaxValue - 1, long.MaxValue).Sample(weight =>
        {
            using var manager = new InMemoryCacheManager(new NullCacheMetricsProvider(), Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions
            {
                MaxMemoryBytes = long.MaxValue,
                Weigher = (_, _) => weight,
                EvictionPolicy = eviction,
                EnableBackgroundCleanup = false
            }), new FakeTimeProvider());
            Assert.True(manager.SetAsync("a", 1, TimeSpan.FromMinutes(1)).IsCompletedSuccessfully);
            Assert.True(manager.SetAsync("b", 2, TimeSpan.FromMinutes(1)).IsCompletedSuccessfully);
            Assert.InRange(manager.Engine.TotalWeight, 0, long.MaxValue);
            Assert.True(manager.SetAsync("b", 3, TimeSpan.FromMinutes(1)).IsCompletedSuccessfully);
            Assert.True(manager.SetAsync("c", 4, TimeSpan.FromMinutes(1)).IsCompletedSuccessfully);
            Assert.InRange(manager.Engine.TotalWeight, 0, long.MaxValue);
            Assert.True(manager.Engine.Count <= 1);
            Assert.True(manager.ClearAsync().IsCompletedSuccessfully);
            Assert.Equal(0, manager.Engine.TotalWeight);
        }, seed: PropertySettings.Seed, iter: 3);
    }
}
