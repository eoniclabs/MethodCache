using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Benchmarks.Core;
using MethodCache.Core;
using MethodCache.Core.Runtime;

namespace MethodCache.Benchmarks.Scenarios;

/// <summary>
/// Quick caching benchmarks for development and testing
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class QuickCachingBenchmarks : SimpleBenchmarkBase
{
    private IBasicCacheService _cacheService = null!;
    private IBasicCacheService _noCacheService = null!;
    private ICacheManager _cacheManager = null!;

    // Minimal parameters for quick testing
    public int DataSize { get; set; } = 1;
    public string ModelType { get; set; } = "Small";

    // Track what benchmark we're running
    private string _currentBenchmark = "";

    protected override void ConfigureBenchmarkServices(IServiceCollection services)
    {
        services.AddSingleton<IBasicCacheService, BasicCacheService>();
        services.AddSingleton<BasicCacheService>();
    }

    protected override void OnSetupComplete()
    {
        _cacheService = ServiceProvider.GetRequiredService<IBasicCacheService>();
        _noCacheService = ServiceProvider.GetRequiredService<BasicCacheService>();
        _cacheManager = ServiceProvider.GetRequiredService<ICacheManager>();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Setup cache state based on which benchmark is running
        var cacheKey = $"GetDataAsync_{DataSize}_{ModelType}";

        if (_currentBenchmark == nameof(CacheHit))
        {
            // Warm up cache for hit tests
            var warmupTask = _cacheService.GetDataAsync(DataSize, ModelType);
            warmupTask.Wait();
        }
        else if (_currentBenchmark == nameof(CacheMiss))
        {
            // Clear cache for miss tests
            var clearTask = _cacheManager.InvalidateByKeysAsync(cacheKey);
            clearTask.Wait();
        }
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        // Clean up after each iteration if needed
        _currentBenchmark = "";
    }

    [Benchmark(Baseline = true)]
    public async Task<object> NoCaching()
    {
        _currentBenchmark = nameof(NoCaching);
        return await _noCacheService.GetDataAsync(DataSize, ModelType);
    }

    [Benchmark]
    public async Task<object> CacheMiss()
    {
        _currentBenchmark = nameof(CacheMiss);
        // Cache already cleared in IterationSetup - just measure the miss
        return await _cacheService.GetDataAsync(DataSize, ModelType);
    }

    [Benchmark]
    public async Task<object> CacheHit()
    {
        _currentBenchmark = nameof(CacheHit);
        // Cache already warmed in IterationSetup - just measure the hit
        return await _cacheService.GetDataAsync(DataSize, ModelType);
    }
}