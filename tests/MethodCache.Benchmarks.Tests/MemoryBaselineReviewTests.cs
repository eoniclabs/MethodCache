using MethodCache.Benchmarks.Scenarios;
using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Benchmarks.Tests;

public class MemoryBaselineReviewTests
{
    [Fact]
    public async Task NoCachingBaseline_DoesNotPopulateTheCache()
    {
        var benchmark = new Probe { CacheSize = 2, DataType = "Small" };
        benchmark.GlobalSetup();
        try
        {
            Assert.Equal(0, benchmark.Manager.Engine.Count);
            await benchmark.NoCaching_MemoryBaseline();
            Assert.Equal(0, benchmark.Manager.Engine.Count);
        }
        finally
        {
            benchmark.GlobalCleanup();
        }
    }

    private sealed class Probe : MemoryUsageBenchmarks
    {
        public InMemoryCacheManager Manager => ServiceProvider.GetRequiredService<InMemoryCacheManager>();
    }
}
