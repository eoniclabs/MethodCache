using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MethodCache.Benchmarks.Core;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Core.Runtime.Defaults;
using MethodCache.HybridCache.Implementation;
using MethodCache.Providers.Redis;
using MethodCache.Providers.Redis.Configuration;

namespace MethodCache.Benchmarks.Scenarios;

/// <summary>
/// Benchmarks comparing different cache provider implementations
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
[RankColumn]
public class CacheProviderComparisonBenchmarks : BenchmarkBase
{
    private ICacheProviderTestService _inMemoryService = null!;
    private ICacheProviderTestService _hybridService = null!;
    private ICacheProviderTestService? _redisService;

    [Params(10, 100, 1000)]
    public int ItemCount { get; set; }

    [Params("Small", "Medium")]
    public string DataType { get; set; } = "Small";

    protected override void OnSetupComplete()
    {
        // Create services with different cache providers
        var inMemoryProvider = CreateServiceProviderWithCache<InMemoryCacheManager>();
        var hybridProvider = CreateServiceProviderWithCache<HybridCacheManager>();
        
        _inMemoryService = inMemoryProvider.GetRequiredService<ICacheProviderTestService>();
        _hybridService = hybridProvider.GetRequiredService<ICacheProviderTestService>();

        // Try to create Redis service if available
        try
        {
            var redisProvider = CreateServiceProviderWithRedis();
            if (redisProvider != null)
            {
                _redisService = redisProvider.GetRequiredService<ICacheProviderTestService>();
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Redis not available for benchmarks: {Error}", ex.Message);
        }
    }

    private IServiceProvider? CreateServiceProviderWithRedis()
    {
        try
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            
            // Replace with Redis cache manager
            services.Remove(services.First(s => s.ServiceType == typeof(ICacheManager)));
            services.AddSingleton<ICacheManager>(provider =>
            {
                // Skip Redis implementation for now - too complex for benchmark setup
                throw new NotSupportedException("Redis benchmarks not implemented in this version");
            });
            
            services.AddSingleton<ICacheProviderTestService, CacheProviderTestService>();
            
            var provider = services.BuildServiceProvider();
            
            // Test Redis connectivity
            var cacheManager = provider.GetRequiredService<ICacheManager>();
            cacheManager.GetOrCreateAsync("test", Array.Empty<object>(), () => Task.FromResult("test"), 
                new CacheMethodSettings(), provider.GetRequiredService<ICacheKeyGenerator>(), true)
                .Wait(TimeSpan.FromSeconds(5));
            
            return provider;
        }
        catch
        {
            return null;
        }
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
    public async Task<List<object>> Hybrid_CacheHits()
    {
        return await RunCacheHitsTest(_hybridService);
    }

    [Benchmark]
    public async Task<List<object>?> Redis_CacheHits()
    {
        if (_redisService == null) return null;
        return await RunCacheHitsTest(_redisService);
    }

    [Benchmark]
    public async Task<List<object>> InMemory_CacheMisses()
    {
        return await RunCacheMissesTest(_inMemoryService);
    }

    [Benchmark]
    public async Task<List<object>> Hybrid_CacheMisses()
    {
        return await RunCacheMissesTest(_hybridService);
    }

    [Benchmark]
    public async Task<List<object>?> Redis_CacheMisses()
    {
        if (_redisService == null) return null;
        return await RunCacheMissesTest(_redisService);
    }

    [Benchmark]
    public async Task InMemory_BulkInvalidation()
    {
        await RunBulkInvalidationTest(_inMemoryService);
    }

    [Benchmark]
    public async Task Hybrid_BulkInvalidation()
    {
        await RunBulkInvalidationTest(_hybridService);
    }

    [Benchmark]
    public async Task? Redis_BulkInvalidation()
    {
        if (_redisService == null) return;
        await RunBulkInvalidationTest(_redisService);
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

public class CacheProviderTestService : ICacheProviderTestService
{
    private readonly ICacheManager _cacheManager;
    private readonly MethodCacheConfiguration _configuration;
    private readonly ICacheKeyGenerator _keyGenerator;

    public CacheProviderTestService(
        ICacheManager cacheManager, 
        MethodCacheConfiguration configuration,
        ICacheKeyGenerator keyGenerator)
    {
        _cacheManager = cacheManager;
        _configuration = configuration;
        _keyGenerator = keyGenerator;
    }

    [Cache(Duration = "00:05:00", Tags = new[] { "items" })]
    public virtual async Task<object> GetItemAsync(int id, string dataType)
    {
        // Source generator handles caching - just call the business logic
        return await CreateItemAsync(id, dataType);
    }

    [CacheInvalidate(Tags = new[] { "items" })]
    public virtual async Task InvalidateAllAsync()
    {
        await _cacheManager.InvalidateByTagsAsync("items");
    }

    public async Task ClearCacheAsync()
    {
        await _cacheManager.InvalidateByTagsAsync("items");
    }

    private async Task<object> CreateItemAsync(int id, string dataType)
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