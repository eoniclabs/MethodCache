using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using MethodCache.Benchmarks.Comparison.Services;
using MethodCache.Core.Infrastructure.Extensions;
using MethodCache.Providers.Memory.Extensions;
using MethodCache.Providers.FastCache.Extensions;

namespace MethodCache.Benchmarks.Comparison;

/// <summary>
/// Real-world comparison showing actual MethodCache performance (via source generation)
/// vs Microsoft.Extensions.Caching.Memory baseline
/// Includes Core InMemory, AdvancedMemory, and FastCache providers
///
/// IMPORTANT: This benchmark shows how MethodCache is actually used by developers.
/// Results: MethodCache 58-166 ns vs baseline 468-766 ns (5-13x faster)
///
/// For adapter-based comparison (normalized but with overhead), see UnifiedCacheComparisonBenchmarks.cs
/// For comprehensive explanation, see Comparison/README.md and BENCHMARKING_GUIDE.md
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 5)]
[RankColumn]
public class RealMethodCacheComparison
{
    private const string TestKey = "test_key";
    private static readonly SamplePayload TestPayload = new() { Id = 1, Name = "Test", Data = new byte[1024] };

    private ICachedDataService _realMethodCache = null!;
    private ICachedDataService _advancedMemoryCache = null!;
    private ICachedDataService _fastCache = null!;
    private IMemoryCache _baselineCache = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Setup real MethodCache with Core InMemory provider (default)
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMethodCache(config =>
        {
            config.DefaultPolicy(builder => builder.WithDuration(TimeSpan.FromMinutes(10)));
        }, typeof(RealMethodCacheService).Assembly);
        services.AddSingleton<ICachedDataService, RealMethodCacheService>();
        services.AddSingleton<RealMethodCacheService>();

        var serviceProvider = services.BuildServiceProvider();
        _realMethodCache = serviceProvider.GetRequiredService<ICachedDataService>();

        // Setup MethodCache with AdvancedMemory provider
        var advancedServices = new ServiceCollection();
        advancedServices.AddLogging();
        advancedServices.AddMethodCache(config =>
        {
            config.DefaultPolicy(builder => builder.WithDuration(TimeSpan.FromMinutes(10)));
        }, typeof(AdvancedMemoryCachedService).Assembly);
        advancedServices.AddAdvancedMemoryStorage(); // Use the sophisticated Memory provider
        advancedServices.AddSingleton<ICachedDataService, AdvancedMemoryCachedService>();
        advancedServices.AddSingleton<AdvancedMemoryCachedService>();

        var advancedServiceProvider = advancedServices.BuildServiceProvider();
        _advancedMemoryCache = advancedServiceProvider.GetRequiredService<ICachedDataService>();

        // Setup MethodCache with FastCache provider (ultra-fast minimal overhead)
        var fastServices = new ServiceCollection();
        fastServices.AddLogging();
        fastServices.AddMethodCache(config =>
        {
            config.DefaultPolicy(builder => builder.WithDuration(TimeSpan.FromMinutes(10)));
        }, typeof(FastCacheCachedService).Assembly);
        fastServices.AddFastCacheStorage(); // Use the ultra-fast FastCache provider
        fastServices.AddSingleton<ICachedDataService, FastCacheCachedService>();
        fastServices.AddSingleton<FastCacheCachedService>();

        var fastServiceProvider = fastServices.BuildServiceProvider();
        _fastCache = fastServiceProvider.GetRequiredService<ICachedDataService>();

        // Setup baseline Microsoft.Extensions.Caching.Memory
        _baselineCache = new MemoryCache(new MemoryCacheOptions());

        // Warmup all caches
        _realMethodCache.GetData(TestKey);
        _advancedMemoryCache.GetData(TestKey);
        _fastCache.GetData(TestKey);
        _baselineCache.Set(TestKey, TestPayload, TimeSpan.FromMinutes(10));
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Ensure all caches are warm for every iteration
        _realMethodCache.GetData(TestKey);
        _advancedMemoryCache.GetData(TestKey);
        _fastCache.GetData(TestKey);
        _baselineCache.Set(TestKey, TestPayload, TimeSpan.FromMinutes(10));
    }

    [Benchmark]
    public SamplePayload MethodCache_CoreInMemory_Hit()
    {
        // Uses Core InMemoryCacheManager (basic provider)
        return _realMethodCache.GetData(TestKey);
    }

    [Benchmark]
    public SamplePayload MethodCache_AdvancedMemory_Hit()
    {
        // Uses AdvancedMemoryStorage provider (with tags, stats, etc.)
        return _advancedMemoryCache.GetData(TestKey);
    }

    [Benchmark]
    public SamplePayload MethodCache_FastCache_Hit()
    {
        // Uses FastCacheStorage provider (ultra-fast minimal overhead)
        return _fastCache.GetData(TestKey);
    }

    [Benchmark(Baseline = true)]
    public SamplePayload Baseline_MemoryCache_Hit()
    {
        return _baselineCache.Get<SamplePayload>(TestKey)!;
    }

    [Benchmark]
    public async Task<SamplePayload> MethodCache_CoreInMemory_HitAsync()
    {
        return await _realMethodCache.GetDataAsync(TestKey);
    }

    [Benchmark]
    public async Task<SamplePayload> MethodCache_AdvancedMemory_HitAsync()
    {
        return await _advancedMemoryCache.GetDataAsync(TestKey);
    }

    [Benchmark]
    public async Task<SamplePayload> MethodCache_FastCache_HitAsync()
    {
        return await _fastCache.GetDataAsync(TestKey);
    }

    [Benchmark]
    public async Task<SamplePayload> Baseline_MemoryCache_HitAsync()
    {
        return await Task.FromResult(_baselineCache.Get<SamplePayload>(TestKey)!);
    }
}
