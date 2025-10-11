using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Benchmarks.Core;
using MethodCache.Core;
using MethodCache.Core.Runtime.Defaults;
using MethodCache.Abstractions.Registry;
using MethodCache.Benchmarks.Infrastructure;
using System.Collections.Concurrent;

namespace MethodCache.Benchmarks.Scenarios;

/// <summary>
/// Benchmarks testing concurrent access patterns and thread safety
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
[RankColumn]
public class ConcurrentAccessBenchmarks : BenchmarkBase
{
    private IConcurrentCacheService _service = null!;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    [Params(2, 4, 8, 16)]
    public int ThreadCount { get; set; }

    [Params(100, 1000)]
    public int OperationsPerThread { get; set; }

    protected override void ConfigureBenchmarkServices(IServiceCollection services)
    {
        services.AddSingleton<IConcurrentCacheService, ConcurrentCacheService>();
    }

    protected override void OnSetupComplete()
    {
        _service = ServiceProvider.GetRequiredService<IConcurrentCacheService>();
    }

    [Benchmark(Baseline = true)]
    public async Task ConcurrentCacheHits()
    {
        // Pre-warm cache
        for (int i = 0; i < 10; i++)
        {
            await _service.GetDataAsync(i);
        }

        var tasks = new List<Task>();
        for (int t = 0; t < ThreadCount; t++)
        {
            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < OperationsPerThread; i++)
                {
                    await _service.GetDataAsync(i % 10);
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    [Benchmark]
    public async Task ConcurrentCacheMisses()
    {
        var tasks = new List<Task>();
        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < OperationsPerThread; i++)
                {
                    // Use unique keys to ensure cache misses
                    await _service.GetDataAsync(threadId * 10000 + i);
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    [Benchmark]
    public async Task ConcurrentMixedOperations()
    {
        var tasks = new List<Task>();
        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < OperationsPerThread; i++)
                {
                    if (i % 10 == 0)
                    {
                        // 10% invalidations
                        await _service.InvalidateDataAsync(i % 100);
                    }
                    else
                    {
                        // 90% reads
                        await _service.GetDataAsync(i % 100);
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    [Benchmark]
    public async Task ConcurrentSameKeyAccess()
    {
        const int sameKey = 42;
        
        var tasks = new List<Task<SmallModel>>();
        for (int t = 0; t < ThreadCount; t++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var results = new List<SmallModel>();
                for (int i = 0; i < OperationsPerThread; i++)
                {
                    results.Add(await _service.GetDataAsync(sameKey));
                }
                return results.First(); // Return one result to avoid large allocations
            }));
        }

        await Task.WhenAll(tasks);
    }

    [Benchmark]
    public async Task ConcurrentBulkInvalidation()
    {
        // Pre-warm cache
        for (int i = 0; i < 100; i++)
        {
            await _service.GetDataAsync(i);
        }

        var tasks = new List<Task>();
        
        // Mix of reads and bulk invalidations
        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            if (threadId == 0)
            {
                // One thread doing bulk invalidations
                tasks.Add(Task.Run(async () =>
                {
                    for (int i = 0; i < OperationsPerThread / 10; i++)
                    {
                        await _service.BulkInvalidateAsync();
                        await Task.Delay(10); // Small delay between invalidations
                    }
                }));
            }
            else
            {
                // Other threads doing reads
                tasks.Add(Task.Run(async () =>
                {
                    for (int i = 0; i < OperationsPerThread; i++)
                    {
                        await _service.GetDataAsync(i % 100);
                    }
                }));
            }
        }

        await Task.WhenAll(tasks);
    }

    [Benchmark]
    public async Task ConcurrentCacheStampede()
    {
        // Simulate cache stampede - multiple threads trying to access the same expired item
        const int stampedeKey = 999;
        
        // Clear cache to ensure miss
        await _service.InvalidateDataAsync(stampedeKey);

        var tasks = new List<Task<SmallModel>>();
        for (int t = 0; t < ThreadCount; t++)
        {
            tasks.Add(Task.Run(async () => await _service.GetSlowDataAsync(stampedeKey)));
        }

        await Task.WhenAll(tasks);
    }
}

public interface IConcurrentCacheService
{
    Task<SmallModel> GetDataAsync(int id);
    Task<SmallModel> GetSlowDataAsync(int id);
    Task InvalidateDataAsync(int id);
    Task BulkInvalidateAsync();
}

public class ConcurrentCacheService : IConcurrentCacheService
{
    private readonly ICacheManager _cacheManager;
    private readonly IPolicyRegistry _policyRegistry;
    private readonly ICacheKeyGenerator _keyGenerator;
    private static readonly ConcurrentDictionary<int, int> _callCounts = new();

    public ConcurrentCacheService(
        ICacheManager cacheManager,
        IPolicyRegistry policyRegistry,
        ICacheKeyGenerator keyGenerator)
    {
        _cacheManager = cacheManager;
        _policyRegistry = policyRegistry;
        _keyGenerator = keyGenerator;
    }

    [Cache(Duration = "00:02:00", Tags = new[] { "data" })]
    public virtual async Task<SmallModel> GetDataAsync(int id)
    {
        var settings = _policyRegistry.GetSettingsFor<ConcurrentCacheService>(nameof(GetDataAsync));
        var args = new object[] { id };

        return await _cacheManager.GetOrCreateAsync<SmallModel>(
            "GetDataAsync",
            args,
            async () => await CreateDataAsync(id),
            settings,
            _keyGenerator);
    }

    [Cache(Duration = "00:02:00", Tags = new[] { "slow_data" })]
    public virtual async Task<SmallModel> GetSlowDataAsync(int id)
    {
        var settings = _policyRegistry.GetSettingsFor<ConcurrentCacheService>(nameof(GetSlowDataAsync));
        var args = new object[] { id };

        return await _cacheManager.GetOrCreateAsync<SmallModel>(
            "GetSlowDataAsync",
            args,
            async () => await CreateSlowDataAsync(id),
            settings,
            _keyGenerator);
    }

    [CacheInvalidate(Tags = new[] { "data" })]
    public virtual async Task InvalidateDataAsync(int id)
    {
        await _cacheManager.InvalidateByTagsAsync("data");
    }

    [CacheInvalidate(Tags = new[] { "data", "slow_data" })]
    public virtual async Task BulkInvalidateAsync()
    {
        await _cacheManager.InvalidateByTagsAsync("data", "slow_data");
    }

    private async Task<SmallModel> CreateDataAsync(int id)
    {
        // Track call count for analysis
        _callCounts.AddOrUpdate(id, 1, (key, count) => count + 1);
        
        // Simulate fast operation
        await Task.Yield();
        return SmallModel.Create(id);
    }

    private async Task<SmallModel> CreateSlowDataAsync(int id)
    {
        // Track call count for stampede analysis
        _callCounts.AddOrUpdate(id, 1, (key, count) => count + 1);
        
        // Simulate slow operation (database query, API call, etc.)
        await Task.Delay(100);
        return SmallModel.Create(id);
    }

    public static Dictionary<int, int> GetCallCounts() => 
        new(_callCounts);

    public static void ResetCallCounts() => 
        _callCounts.Clear();
}
