using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using MethodCache.Benchmarks.Core;
using MethodCache.Core;
using System.Runtime.CompilerServices;
using MethodCache.Core.Runtime;
using LazyCache;
using LazyCache.Providers;

namespace MethodCache.Benchmarks.Scenarios;

/// <summary>
/// Baseline comparison benchmarks against established caching libraries
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
[RankColumn]
public class BaselineComparisonBenchmarks : SimpleBenchmarkBase
{
    // MethodCache services
    private IBasicCacheService _methodCacheService = null!;
    private ICacheManager _cacheManager = null!;

    // Microsoft.Extensions.Caching.Memory
    private IMemoryCache _memoryCache = null!;

    // LazyCache
    private IAppCache _lazyCache = null!;

    // Test parameters
    private string _cacheKey = null!;
    private object _testData = null!;

    // Simplified parameters for quick testing - uncomment for full testing
    //[Params(1, 10, 100, 1000)]
    //public int DataSize { get; set; }
    //[Params("Small", "Medium", "Large")]
    //public string ModelType { get; set; } = "Small";

    // Use fixed values for now
    public int DataSize { get; set; } = 100;
    public string ModelType { get; set; } = "Small";

    protected override void ConfigureBenchmarkServices(IServiceCollection services)
    {
        services.AddSingleton<IBasicCacheService, BasicCacheService>();
        services.AddSingleton<BasicCacheService>();

        // Add Microsoft.Extensions.Caching.Memory
        services.AddMemoryCache(options =>
        {
            options.SizeLimit = null; // No size limit for fair comparison
            options.CompactionPercentage = 0.05;
            options.ExpirationScanFrequency = TimeSpan.FromMinutes(5);
        });

        // Add LazyCache
        services.AddLazyCache();
    }

    protected override void OnSetupComplete()
    {
        _methodCacheService = ServiceProvider.GetRequiredService<IBasicCacheService>();
        _cacheManager = ServiceProvider.GetRequiredService<ICacheManager>();
        _memoryCache = ServiceProvider.GetRequiredService<IMemoryCache>();
        _lazyCache = ServiceProvider.GetRequiredService<IAppCache>();

        // Pre-create test data to avoid measurement noise
        _testData = CreateTestData(DataSize, ModelType);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Setup unique cache key for this iteration
        _cacheKey = $"benchmark_{DataSize}_{ModelType}_{Guid.NewGuid()}";

        // Ensure all caches are warmed up identically for cache hit tests
        WarmupCaches();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        // Clean up after each iteration
        _memoryCache.Remove(_cacheKey);
        _lazyCache.Remove(_cacheKey);
        // MethodCache cleanup happens automatically via cache manager
    }

    private void WarmupCaches()
    {
        // Warm up all caches with the same data for fair comparison
        _memoryCache.Set(_cacheKey, _testData, TimeSpan.FromMinutes(10));
        _lazyCache.Add(_cacheKey, _testData, TimeSpan.FromMinutes(10));

        // For MethodCache, we need to call the service once to populate
        // This is done outside measurement in IterationSetup
        var warmupTask = _methodCacheService.GetDataAsync(DataSize, ModelType);
        warmupTask.Wait();
    }

    // ===== CACHE HIT BENCHMARKS =====

    [BenchmarkCategory("CacheHit")]
    [Benchmark]
    public object? MemoryCache_Hit()
    {
        return _memoryCache.Get<object>(_cacheKey);
    }

    [BenchmarkCategory("CacheHit")]
    [Benchmark]
    public object? LazyCache_Hit()
    {
        return _lazyCache.Get<object>(_cacheKey);
    }

    [BenchmarkCategory("CacheHit")]
    [Benchmark]
    public async Task<object> MethodCache_Hit()
    {
        // This should hit the cache since we warmed it up in IterationSetup
        return await _methodCacheService.GetDataAsync(DataSize, ModelType);
    }

    // ===== CACHE MISS BENCHMARKS =====

    [BenchmarkCategory("CacheMiss")]
    [Benchmark]
    public async Task<object?> MemoryCache_Miss()
    {
        var missKey = $"miss_{_cacheKey}";
        return await _memoryCache.GetOrCreateAsync(missKey, async entry =>
        {
            entry.SetAbsoluteExpiration(TimeSpan.FromMinutes(10));
            return await CreateTestDataAsync(DataSize, ModelType);
        });
    }

    [BenchmarkCategory("CacheMiss")]
    [Benchmark]
    public async Task<object> LazyCache_Miss()
    {
        var missKey = $"miss_{_cacheKey}";
        return await _lazyCache.GetOrAddAsync(missKey, async () =>
        {
            return await CreateTestDataAsync(DataSize, ModelType);
        }, TimeSpan.FromMinutes(10));
    }

    [BenchmarkCategory("CacheMiss")]
    [Benchmark]
    public async Task<object> MethodCache_Miss()
    {
        // Clear cache to ensure miss
        await _cacheManager.InvalidateByKeysAsync($"GetDataAsync_{DataSize}_{ModelType}");
        return await _methodCacheService.GetDataAsync(DataSize, ModelType);
    }

    // ===== CONCURRENT ACCESS BENCHMARKS =====

    [BenchmarkCategory("Concurrent")]
    [Benchmark]
    public async Task MemoryCache_Concurrent()
    {
        var tasks = new Task<object?>[10];
        for (int i = 0; i < 10; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                return await _memoryCache.GetOrCreateAsync(_cacheKey, async entry =>
                {
                    entry.SetAbsoluteExpiration(TimeSpan.FromMinutes(10));
                    return await CreateTestDataAsync(DataSize, ModelType);
                });
            });
        }
        await Task.WhenAll(tasks);
    }

    [BenchmarkCategory("Concurrent")]
    [Benchmark]
    public async Task LazyCache_Concurrent()
    {
        var tasks = new Task<object>[10];
        for (int i = 0; i < 10; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                return await _lazyCache.GetOrAddAsync(_cacheKey, async () =>
                {
                    return await CreateTestDataAsync(DataSize, ModelType);
                }, TimeSpan.FromMinutes(10));
            });
        }
        await Task.WhenAll(tasks);
    }

    [BenchmarkCategory("Concurrent")]
    [Benchmark]
    public async Task MethodCache_Concurrent()
    {
        var tasks = new Task<object>[10];
        for (int i = 0; i < 10; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                return await _methodCacheService.GetDataAsync(DataSize, ModelType);
            });
        }
        await Task.WhenAll(tasks);
    }

    // ===== CACHE STAMPEDE PROTECTION =====

    [BenchmarkCategory("Stampede")]
    [Benchmark]
    public async Task MemoryCache_Stampede()
    {
        // Simulate cache stampede - no built-in protection
        var stampedeKey = $"stampede_{_cacheKey}";
        _memoryCache.Remove(stampedeKey); // Ensure miss

        var tasks = new Task<object?>[20];
        for (int i = 0; i < 20; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                return await _memoryCache.GetOrCreateAsync(stampedeKey, async entry =>
                {
                    entry.SetAbsoluteExpiration(TimeSpan.FromMinutes(10));
                    await Task.Delay(50); // Simulate expensive operation
                    return await CreateTestDataAsync(DataSize, ModelType);
                });
            });
        }
        await Task.WhenAll(tasks);
    }

    [BenchmarkCategory("Stampede")]
    [Benchmark]
    public async Task LazyCache_Stampede()
    {
        // LazyCache has built-in stampede protection
        var stampedeKey = $"stampede_{_cacheKey}";
        _lazyCache.Remove(stampedeKey); // Ensure miss

        var tasks = new Task<object>[20];
        for (int i = 0; i < 20; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                return await _lazyCache.GetOrAddAsync(stampedeKey, async () =>
                {
                    await Task.Delay(50); // Simulate expensive operation
                    return await CreateTestDataAsync(DataSize, ModelType);
                }, TimeSpan.FromMinutes(10));
            });
        }
        await Task.WhenAll(tasks);
    }

    [BenchmarkCategory("Stampede")]
    [Benchmark]
    public async Task MethodCache_Stampede()
    {
        // Test MethodCache stampede protection
        var stampedeKey = $"GetSlowDataAsync_{DataSize}_{ModelType}";
        await _cacheManager.InvalidateByKeysAsync(stampedeKey);

        var tasks = new Task<object>[20];
        for (int i = 0; i < 20; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                return await _methodCacheService.GetSlowDataAsync(DataSize, ModelType);
            });
        }
        await Task.WhenAll(tasks);
    }

    // ===== HELPER METHODS =====

    private object CreateTestData(int size, string modelType)
    {
        return modelType switch
        {
            "Small" => Enumerable.Range(0, size).Select(i => SmallModel.Create(i)).ToList(),
            "Medium" => Enumerable.Range(0, size).Select(i => MediumModel.Create(i)).ToList(),
            "Large" => Enumerable.Range(0, size).Select(i => LargeModel.Create(i)).ToList(),
            _ => throw new ArgumentException($"Unknown model type: {modelType}")
        };
    }

    private async Task<object> CreateTestDataAsync(int size, string modelType)
    {
        await Task.Delay(1); // Simulate async work
        return CreateTestData(size, modelType);
    }
}

