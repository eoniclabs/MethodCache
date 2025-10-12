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

    [Benchmark(Baseline = true)]
    public async Task<object> NoCaching()
    {
        return await _noCacheService.GetDataAsync(DataSize, ModelType);
    }

    [Benchmark]
    public async Task<object> CacheMiss()
    {
        // Clear cache to ensure miss
        await _cacheManager.InvalidateByKeysAsync($"GetDataAsync_{DataSize}_{ModelType}");
        return await _cacheService.GetDataAsync(DataSize, ModelType);
    }

    [Benchmark]
    public async Task<object> CacheHit()
    {
        // Warm up cache first
        await _cacheService.GetDataAsync(DataSize, ModelType);

        // Now measure cache hit
        return await _cacheService.GetDataAsync(DataSize, ModelType);
    }

    [Benchmark]
    public async Task<object> CacheHitCold()
    {
        await _cacheManager.InvalidateByKeysAsync($"GetDataAsync_{DataSize}_{ModelType}");

        // First call to warm cache
        await _cacheService.GetDataAsync(DataSize, ModelType);

        // Second call should hit cache
        return await _cacheService.GetDataAsync(DataSize, ModelType);
    }
}