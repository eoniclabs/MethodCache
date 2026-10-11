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
    private BasicCacheService _baselineService = null!;

    // Minimal parameters for quick testing
    public int DataSize { get; set; } = 1;
    public string ModelType { get; set; } = "Small";

    protected override void ConfigureBenchmarkServices(IServiceCollection services)
    {
        services.AddSingleton<BasicCacheService>();
        services.AddIBasicCacheServiceWithCaching(sp => sp.GetRequiredService<BasicCacheService>());
    }

    protected override void OnSetupComplete()
    {
        _cacheService = ServiceProvider.GetRequiredService<IBasicCacheService>();
        _baselineService = ServiceProvider.GetRequiredService<BasicCacheService>();
    }

    [IterationSetup(Target = nameof(CacheMiss))]
    public void SetupCacheMiss()
    {
        _cacheService.InvalidateDataAsync(DataSize, ModelType).GetAwaiter().GetResult();
    }

    [IterationSetup(Target = nameof(CacheHit))]
    public void SetupCacheHit()
    {
        _cacheService.InvalidateDataAsync(DataSize, ModelType).GetAwaiter().GetResult();
        _cacheService.GetDataAsync(DataSize, ModelType).GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true)]
    public async Task<object> NoCaching()
    {
        return await _baselineService.GetDataNoCachingAsync(DataSize, ModelType);
    }

    [Benchmark]
    public async Task<object> CacheMiss()
    {
        return await _cacheService.GetDataAsync(DataSize, ModelType);
    }

    [Benchmark]
    public async Task<object> CacheHit()
    {
        return await _cacheService.GetDataAsync(DataSize, ModelType);
    }
}
