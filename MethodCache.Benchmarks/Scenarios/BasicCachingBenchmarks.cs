using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Benchmarks.Core;
using MethodCache.Core;
using System.Runtime.CompilerServices;
using MethodCache.Core.Runtime;

namespace MethodCache.Benchmarks.Scenarios;

/// <summary>
/// Basic caching performance benchmarks measuring fundamental operations
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
[RankColumn]
public class BasicCachingBenchmarks : SimpleBenchmarkBase
{
    private IBasicCacheService _cacheService = null!;
    private BasicCacheService _baselineService = null!;

    [Params(1, 10, 100, 1000)]
    public int DataSize { get; set; }

    [Params("Small", "Medium", "Large")]
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

    [IterationSetup(Target = nameof(CacheHitCold))]
    public void SetupCacheHitCold()
    {
        _cacheService.InvalidateDataAsync(DataSize, ModelType).GetAwaiter().GetResult();
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
        // Measure hit performance (cache should already be warm from previous iterations)
        return await _cacheService.GetDataAsync(DataSize, ModelType);
    }

    [Benchmark]
    public async Task<object> CacheHitCold()
    {
        // Measure hit performance without warmup
        return await _cacheService.GetDataAsync(DataSize, ModelType);
    }

    [Benchmark]
    public async Task CacheInvalidation()
    {
        // Warm up cache
        await _cacheService.GetDataAsync(DataSize, ModelType);
        // Measure invalidation
        await _cacheService.InvalidateDataAsync(DataSize, ModelType);
    }

    [Benchmark]
    public async Task<List<object>> MultipleCacheHits()
    {
        var results = new List<object>();
        
        // Warm up cache for all items
        for (int i = 0; i < 10; i++)
        {
            await _cacheService.GetDataAsync(i, ModelType);
        }
        
        // Measure multiple hits
        for (int i = 0; i < 10; i++)
        {
            results.Add(await _cacheService.GetDataAsync(i, ModelType));
        }
        
        return results;
    }
}

public interface IBasicCacheService
{
    [Cache(Duration = "00:10:00", Tags = new[] { "data" }, RequireIdempotent = false)]
    Task<object> GetDataAsync(int size, string modelType);

    [Cache(Duration = "00:10:00", Tags = new[] { "data" }, RequireIdempotent = false)]
    Task<object> GetSlowDataAsync(int size, string modelType);

    [CacheInvalidate(Tags = new[] { "data" })]
    Task InvalidateDataAsync(int size, string modelType);
}

public class BasicCacheService : IBasicCacheService
{
    private readonly ICacheManager _cacheManager;

    public BasicCacheService(
        ICacheManager cacheManager)
    {
        _cacheManager = cacheManager;
    }

    public async Task<object> GetDataAsync(int size, string modelType)
    {
        return await CreateDataAsync(size, modelType);
    }

    // No-caching baseline for benchmarks
    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task<object> GetDataNoCachingAsync(int size, string modelType)
    {
        return await CreateDataAsync(size, modelType);
    }

    public async Task<object> GetSlowDataAsync(int size, string modelType)
    {
        // Simulate slow operation for stampede testing
        await Task.Delay(50);
        return await CreateDataAsync(size, modelType);
    }

    public async Task InvalidateDataAsync(int size, string modelType)
    {
        await _cacheManager.InvalidateByTagsAsync("data");
    }

    private async Task<object> CreateDataAsync(int size, string modelType)
    {
        // Simulate work
        await Task.Delay(1);

        return modelType switch
        {
            "Small" => Enumerable.Range(0, size).Select(i => SmallModel.Create(i)).ToList(),
            "Medium" => Enumerable.Range(0, size).Select(i => MediumModel.Create(i)).ToList(),
            "Large" => Enumerable.Range(0, size).Select(i => LargeModel.Create(i)).ToList(),
            _ => throw new ArgumentException($"Unknown model type: {modelType}")
        };
    }
}
