using MethodCache.Benchmarks.Scenarios;
using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Benchmarks.Tests;

public class MemoryScenarioReviewTests
{
    [Fact]
    public async Task FrequentInvalidation_ActuallyReloadsEachBatch()
    {
        var benchmark = new Probe { CacheSize = 100, DataType = "Small" };
        benchmark.GlobalSetup();
        try
        {
            await benchmark.FrequentInvalidation();
            Assert.Equal(100, (await benchmark.Manager.GetStatsAsync()).Misses);
        }
        finally { benchmark.GlobalCleanup(); }
    }

    [Fact]
    public async Task CacheFillUp_ActuallyFillsOnSuccessiveInvocations()
    {
        var benchmark = new Probe { CacheSize = 100, DataType = "Small" };
        benchmark.GlobalSetup();
        try
        {
            await benchmark.CacheFillUp();
            var before = (await benchmark.Manager.GetStatsAsync()).Misses;
            await benchmark.CacheFillUp();
            Assert.Equal(100, (await benchmark.Manager.GetStatsAsync()).Misses - before);
        }
        finally { benchmark.GlobalCleanup(); }
    }

    private sealed class Probe : MemoryUsageBenchmarks
    {
        public InMemoryCacheManager Manager => ServiceProvider.GetRequiredService<InMemoryCacheManager>();
    }
}
