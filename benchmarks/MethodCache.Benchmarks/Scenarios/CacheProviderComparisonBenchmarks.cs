using BenchmarkDotNet.Attributes;
using MethodCache.Benchmarks.Core;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Benchmarks.Scenarios;

/// <summary>
/// Benchmarks for implemented in-memory provider scenarios
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
[RankColumn]
public class CacheProviderComparisonBenchmarks : BenchmarkBase
{
    private ICacheProviderTestService _inMemoryService = null!;
    [Params(10, 100, 1000)]
    public int ItemCount { get; set; }

    [Params("Small", "Medium")]
    public string DataType { get; set; } = "Small";

    protected override void OnSetupComplete()
    {
        _inMemoryService = ServiceProvider.GetRequiredService<ICacheProviderTestService>();
        // Populate outside the measured hit method, including the first iteration.
        for (var i = 0; i < ItemCount; i++)
            _inMemoryService.GetItemAsync(i, DataType).GetAwaiter().GetResult();
    }

    [IterationSetup(Target = nameof(InMemory_CacheHits))]
    public void WarmHits()
    {
        for (var i = 0; i < ItemCount; i++)
            _inMemoryService.GetItemAsync(i, DataType).GetAwaiter().GetResult();
    }

    protected override void ConfigureBenchmarkServices(IServiceCollection services)
    {
        services.AddSingleton<ICacheProviderTestService, CacheProviderTestService>();
    }

    [Benchmark(Baseline = true)]
    public async Task<List<object>> InMemory_CacheHits()
    {
        return await RunCacheHitsTest(_inMemoryService);
    }

    [Benchmark]
    public async Task<List<object>> InMemory_CacheMisses()
    {
        return await RunCacheMissesTest(_inMemoryService);
    }

    [Benchmark]
    public async Task InMemory_BulkInvalidation()
    {
        await RunBulkInvalidationTest(_inMemoryService);
    }

    private async Task<List<object>> RunCacheHitsTest(ICacheProviderTestService service)
    {
        var results = new List<object>();

        // Measure cache hits (cache should already be warm from previous benchmark iterations)
        for (int i = 0; i < ItemCount; i++)
        {
            results.Add(await service.GetItemAsync(i, DataType));
        }

        return results;
    }

    private async Task<List<object>> RunCacheMissesTest(ICacheProviderTestService service)
    {
        var results = new List<object>();

        // Ensure cache is clear
        await service.ClearCacheAsync();

        // Measure cache misses
        for (int i = 0; i < ItemCount; i++)
        {
            results.Add(await service.GetItemAsync(i, DataType));
        }

        return results;
    }

    private async Task RunBulkInvalidationTest(ICacheProviderTestService service)
    {
        // Warm up cache
        for (int i = 0; i < ItemCount; i++)
        {
            await service.GetItemAsync(i, DataType);
        }

        // Measure bulk invalidation
        await service.InvalidateAllAsync();
    }
}

public interface ICacheProviderTestService
{
    Task<object> GetItemAsync(int id, string dataType);
    Task InvalidateAllAsync();
    Task ClearCacheAsync();
}

internal class CacheProviderTestService : ICacheProviderTestService
{
    private readonly ICacheManager _cacheManager;
    private readonly ICacheKeyGenerator _keyGenerator;

    public CacheProviderTestService(
        ICacheManager cacheManager,
        ICacheKeyGenerator keyGenerator)
    {
        _cacheManager = cacheManager;
        _keyGenerator = keyGenerator;
    }

    public virtual async Task<object> GetItemAsync(int id, string dataType)
    {
        var policy = CacheRuntimePolicy.Empty(nameof(GetItemAsync)) with
        {
            Duration = TimeSpan.FromMinutes(10),
            Tags = new[] { "items" }
        };
        return await _cacheManager.GetOrCreateAsync(nameof(GetItemAsync), new object[] { id, dataType },
            () => CreateItemAsync(id, dataType), policy, _keyGenerator);
    }

    public virtual async Task InvalidateAllAsync()
    {
        await _cacheManager.InvalidateByTagsAsync("items");
    }

    public async Task ClearCacheAsync()
    {
        await _cacheManager.InvalidateByTagsAsync("items");
    }

    protected virtual async Task<object> CreateItemAsync(int id, string dataType)
    {
        // Simulate database/API call
        await Task.Delay(Random.Shared.Next(1, 5));

        return dataType switch
        {
            "Small" => SmallModel.Create(id),
            "Medium" => MediumModel.Create(id),
            "Large" => LargeModel.Create(id),
            _ => throw new ArgumentException($"Unknown data type: {dataType}")
        };
    }
}
