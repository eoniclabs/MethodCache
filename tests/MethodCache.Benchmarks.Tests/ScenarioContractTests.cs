using BenchmarkDotNet.Attributes;
using MethodCache.Benchmarks.Scenarios;
using MethodCache.Benchmarks.Comparison;
using MethodCache.Benchmarks.Core;
using MethodCache.Extensibility;
using MethodCache.Benchmarks.Infrastructure;
using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Benchmarks.Tests;

[CollectionDefinition("Scenario counters", DisableParallelization = true)]
public class ScenarioCounterCollection;

[Collection("Scenario counters")]
public class ScenarioContractTests
{
    [Fact]
    public void SourceGeneratorComparison_Setup_UsesTheExpectedPayload()
    {
        var benchmark = new SourceGenOptimizationBenchmarks();
        try
        {
            Assert.Null(Record.Exception(benchmark.GlobalSetup));
            Assert.Equal(benchmark.MemoryCache_CacheHit().Id, benchmark.SourceGenOptimized_SyncCacheHit().Id);
        }
        finally { benchmark.GlobalCleanup(); }
    }

    [Fact]
    public async Task Stampede_ConsecutiveRuns_InvalidateAndLoadExactlyOnceEach()
    {
        var benchmark = new StampedeProbe { ThreadCount = 4 };
        benchmark.GlobalSetup();
        try
        {
            await benchmark.ConcurrentCacheStampede();
            Assert.Equal(1, benchmark.Service.Calls);
            await benchmark.ConcurrentCacheStampede();
            Assert.Equal(2, benchmark.Service.Calls);
        }
        finally { benchmark.GlobalCleanup(); }
    }
    [Fact]
    public async Task ConcurrentMisses_EveryInvocation_ExecutesEveryFactory()
    {
        var benchmark = new ConcurrentAccessBenchmarks { ThreadCount = 2, OperationsPerThread = 2 };
        benchmark.GlobalSetup();
        try
        {
            ConcurrentCacheService.ResetCallCounts();
            await benchmark.ConcurrentCacheMisses();
            var first = ConcurrentCacheService.GetCallCounts().Values.Sum();
            await benchmark.ConcurrentCacheMisses();
            var second = ConcurrentCacheService.GetCallCounts().Values.Sum() - first;
            Assert.Equal(4, first);
            Assert.Equal(4, second);
        }
        finally { benchmark.GlobalCleanup(); }
    }

    [Fact]
    public async Task ConcurrentInvalidation_RemovesTheDataUsedByTheScenario()
    {
        var benchmark = new ConcurrentProbe();
        benchmark.GlobalSetup();
        try
        {
            ConcurrentCacheService.ResetCallCounts();
            await benchmark.Service.GetDataAsync(999);
            await benchmark.Service.InvalidateDataAsync(999);
            await benchmark.Service.GetDataAsync(999);
            Assert.Equal(2, ConcurrentCacheService.GetCallCounts()[999]);
        }
        finally { benchmark.GlobalCleanup(); }
    }

    [Fact]
    public void ProviderBenchmarks_OnlyImplementedProviders_AreMeasured()
    {
        var measured = typeof(CacheProviderComparisonBenchmarks).GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(BenchmarkAttribute), true).Length != 0).ToArray();
        Assert.NotEmpty(measured);
        Assert.All(measured, m => Assert.StartsWith("InMemory_", m.Name));
    }

    [Fact]
    public async Task ProviderHitScenario_ConsecutiveRuns_ReuseThePopulatedValues()
    {
        var benchmark = new CacheProviderComparisonBenchmarks { ItemCount = 2, DataType = "Small" };
        benchmark.GlobalSetup();
        try
        {
            var first = await benchmark.InMemory_CacheHits();
            var second = await benchmark.InMemory_CacheHits();
            Assert.Equal(2, first.Count);
            Assert.Same(first[0], second[0]);
            Assert.Same(first[1], second[1]);
        }
        finally { benchmark.GlobalCleanup(); }
    }

    [Fact]
    public async Task ProviderMissAndBulkInvalidation_RemoveWarmValues()
    {
        var benchmark = new CacheProviderComparisonBenchmarks { ItemCount = 2, DataType = "Medium" };
        benchmark.GlobalSetup();
        try
        {
            var warm = await benchmark.InMemory_CacheHits();
            var firstMiss = await benchmark.InMemory_CacheMisses();
            Assert.NotSame(warm[0], firstMiss[0]);
            Assert.Same(firstMiss[0], (await benchmark.InMemory_CacheHits())[0]);
            var secondMiss = await benchmark.InMemory_CacheMisses();
            Assert.NotSame(firstMiss[0], secondMiss[0]);
            await benchmark.InMemory_BulkInvalidation();
            Assert.NotSame(secondMiss[0], (await benchmark.InMemory_CacheHits())[0]);
        }
        finally { benchmark.GlobalCleanup(); }
    }

    private sealed class ConcurrentProbe : ConcurrentAccessBenchmarks
    {
        public IConcurrentCacheService Service => ServiceProvider.GetRequiredService<IConcurrentCacheService>();
    }

    private sealed class StampedeProbe : ConcurrentAccessBenchmarks
    {
        public SlowService Service => ServiceProvider.GetRequiredService<SlowService>();
        protected override void ConfigureBenchmarkServices(IServiceCollection services)
        {
            services.AddSingleton<SlowService>();
            services.AddSingleton<IConcurrentCacheService>(sp => sp.GetRequiredService<SlowService>());
        }
    }

    private sealed class SlowService(ICacheManager manager, IPolicyRegistry registry, ICacheKeyGenerator keys)
        : ConcurrentCacheService(manager, registry, keys)
    {
        public int Calls;
        protected override Task<SmallModel> CreateSlowDataAsync(int id)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(SmallModel.Create(id));
        }
    }
}
