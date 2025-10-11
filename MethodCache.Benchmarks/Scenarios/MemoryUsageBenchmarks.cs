using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Benchmarks.Core;
using MethodCache.Core;
using MethodCache.Core.Runtime.Defaults;
using MethodCache.Benchmarks.Infrastructure;
using MethodCache.Abstractions.Registry;

namespace MethodCache.Benchmarks.Scenarios;

/// <summary>
/// Benchmarks measuring memory usage and garbage collection pressure
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
[RankColumn]
public class MemoryUsageBenchmarks : BenchmarkBase
{
    private IMemoryTestService _service = null!;
    private IMemoryTestService _noCacheService = null!;

    [Params(100, 1000, 10000)]
    public int CacheSize { get; set; }

    [Params("Small", "Medium", "Large")]
    public string DataType { get; set; } = "Small";

    protected override void ConfigureBenchmarkServices(IServiceCollection services)
    {
        services.AddSingleton<IMemoryTestService, MemoryTestService>();
        services.AddSingleton<MemoryTestService>();
    }

    protected override void OnSetupComplete()
    {
        _service = ServiceProvider.GetRequiredService<IMemoryTestService>();
        _noCacheService = ServiceProvider.GetRequiredService<MemoryTestService>();
    }

    [Benchmark(Baseline = true)]
    public async Task NoCaching_MemoryBaseline()
    {
        for (int i = 0; i < CacheSize; i++)
        {
            await _noCacheService.GetDataAsync(i, DataType);
        }
    }

    [Benchmark]
    public async Task CacheFillUp()
    {
        // Measure memory impact of filling up cache
        for (int i = 0; i < CacheSize; i++)
        {
            await _service.GetDataAsync(i, DataType);
        }
    }

    [Benchmark]
    public async Task CacheHitsAfterFill()
    {
        // Fill cache first
        for (int i = 0; i < CacheSize; i++)
        {
            await _service.GetDataAsync(i, DataType);
        }

        // Measure memory impact of cache hits
        for (int i = 0; i < CacheSize; i++)
        {
            await _service.GetDataAsync(i, DataType);
        }
    }

    [Benchmark]
    public async Task CacheEvictionPattern()
    {
        // Fill cache beyond capacity to trigger evictions
        for (int i = 0; i < CacheSize * 2; i++)
        {
            await _service.GetDataAsync(i, DataType);
        }
    }

    [Benchmark]
    public async Task FrequentInvalidation()
    {
        for (int batch = 0; batch < 10; batch++)
        {
            // Fill cache
            for (int i = 0; i < CacheSize / 10; i++)
            {
                await _service.GetDataAsync(i, DataType);
            }

            // Invalidate
            await _service.InvalidateAllAsync();
        }
    }

    [Benchmark]
    public async Task LargeObjectCaching()
    {
        // Test memory impact of caching large objects
        for (int i = 0; i < Math.Min(CacheSize, 100); i++)
        {
            await _service.GetLargeObjectAsync(i);
        }
    }

    [Benchmark]
    public async Task ShortLivedCachePattern()
    {
        // Simulate short-lived cache entries that expire quickly
        for (int batch = 0; batch < 10; batch++)
        {
            for (int i = 0; i < CacheSize / 10; i++)
            {
                await _service.GetShortLivedDataAsync(i);
            }
            
            // Wait for expiration
            await Task.Delay(100);
        }
    }

    [Benchmark]
    public async Task MemoryPressureTest()
    {
        // Create memory pressure to test GC behavior
        var tasks = new List<Task>();
        
        for (int thread = 0; thread < 4; thread++)
        {
            int threadId = thread;
            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < CacheSize / 4; i++)
                {
                    await _service.GetDataAsync(threadId * (CacheSize / 4) + i, DataType);
                    
                    // Create additional memory pressure
                    var tempData = new byte[1024];
                    Random.Shared.NextBytes(tempData);
                }
            }));
        }
        
        await Task.WhenAll(tasks);
    }
}

public interface IMemoryTestService
{
    Task<object> GetDataAsync(int id, string dataType);
    Task<LargeModel> GetLargeObjectAsync(int id);
    Task<SmallModel> GetShortLivedDataAsync(int id);
    Task InvalidateAllAsync();
}

public class MemoryTestService : IMemoryTestService
{
    private readonly ICacheManager _cacheManager;
    private readonly IPolicyRegistry _policyRegistry;
    private readonly ICacheKeyGenerator _keyGenerator;

    public MemoryTestService(
        ICacheManager cacheManager,
        IPolicyRegistry policyRegistry,
        ICacheKeyGenerator keyGenerator)
    {
        _cacheManager = cacheManager;
        _policyRegistry = policyRegistry;
        _keyGenerator = keyGenerator;
    }

    [Cache(Duration = "00:10:00", Tags = new[] { "data" })]
    public virtual async Task<object> GetDataAsync(int id, string dataType)
    {
        var settings = _policyRegistry.GetSettingsFor<MemoryTestService>(nameof(GetDataAsync));
        var args = new object[] { id, dataType };

        return await _cacheManager.GetOrCreateAsync<object>(
            "GetDataAsync",
            args,
            async () => await CreateDataAsync(id, dataType),
            settings,
            _keyGenerator);
    }

    [Cache(Duration = "00:05:00", Tags = new[] { "large" })]
    public virtual async Task<LargeModel> GetLargeObjectAsync(int id)
    {
        var settings = _policyRegistry.GetSettingsFor<MemoryTestService>(nameof(GetLargeObjectAsync));
        var args = new object[] { id };

        return await _cacheManager.GetOrCreateAsync<LargeModel>(
            "GetLargeObjectAsync",
            args,
            async () => await CreateLargeObjectAsync(id),
            settings,
            _keyGenerator);
    }

    [Cache(Duration = "00:00:05", Tags = new[] { "short" })] // 5 seconds
    public virtual async Task<SmallModel> GetShortLivedDataAsync(int id)
    {
        var settings = _policyRegistry.GetSettingsFor<MemoryTestService>(nameof(GetShortLivedDataAsync));
        var args = new object[] { id };

        return await _cacheManager.GetOrCreateAsync<SmallModel>(
            "GetShortLivedDataAsync",
            args,
            async () => await CreateShortLivedDataAsync(id),
            settings,
            _keyGenerator);
    }

    [CacheInvalidate(Tags = new[] { "data", "large", "short" })]
    public virtual async Task InvalidateAllAsync()
    {
        await _cacheManager.InvalidateByTagsAsync("data", "large", "short");
    }

    private async Task<object> CreateDataAsync(int id, string dataType)
    {
        await Task.Yield();
        
        return dataType switch
        {
            "Small" => SmallModel.Create(id),
            "Medium" => MediumModel.Create(id),
            "Large" => LargeModel.Create(id),
            _ => throw new ArgumentException($"Unknown data type: {dataType}")
        };
    }

    private async Task<LargeModel> CreateLargeObjectAsync(int id)
    {
        await Task.Yield();
        return LargeModel.Create(id);
    }

    private async Task<SmallModel> CreateShortLivedDataAsync(int id)
    {
        await Task.Yield();
        return SmallModel.Create(id);
    }
}
