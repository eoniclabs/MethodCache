using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core.Infrastructure.Extensions;
using MethodCache.Core.Runtime;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Providers.Memory.Extensions;
using MethodCache.Providers.Memory.Configuration;

namespace MethodCache.Benchmarks.Microbenchmarks;

/// <summary>
/// Direct comparison between Standard Memory Provider (MethodCache.Core) and
/// Advanced Memory Provider (MethodCache.Providers.Memory).
///
/// This benchmark isolates the storage layer to provide a fair comparison
/// between the two memory implementations.
///
/// Standard Memory: InMemoryCacheManager with ConcurrentDictionary-based storage (IMemoryCache interface)
/// Advanced Memory: AdvancedMemoryStorage with LRU/LFU eviction, statistics, tags (IMemoryStorage interface)
///
/// Note: Since the interfaces differ (IMemoryCache is async-only, IMemoryStorage has sync methods),
/// this benchmark focuses on async operations which are common to both.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class MemoryProviderComparisonBenchmarks
{
    private const string TestKey = "benchmark_key";
    private static readonly TestPayload TestPayload = new() { Id = 1, Name = "Test", Data = new byte[1024] };

    // Standard uses IMemoryCache (from InMemoryCacheManager)
    private IMemoryCache _standardMemory = null!;
    // Advanced uses IMemoryStorage (from AdvancedMemoryStorage)
    private IMemoryStorage _advancedMemoryLru = null!;
    private IMemoryStorage _advancedMemoryLfu = null!;
    private IServiceProvider _standardProvider = null!;
    private IServiceProvider _advancedLruProvider = null!;
    private IServiceProvider _advancedLfuProvider = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Standard Memory Provider (from MethodCache.Core)
        // InMemoryCacheManager implements ICacheManager and IMemoryCache
        var standardServices = new ServiceCollection();
        standardServices.AddLogging();
        standardServices.AddMethodCache(config =>
        {
            config.DefaultPolicy(builder => builder.WithDuration(TimeSpan.FromMinutes(10)));
        });
        // Configure for benchmarking - disable statistics
        standardServices.Configure<MethodCache.Core.Configuration.MemoryCacheOptions>(opts =>
        {
            opts.EnableStatistics = false;
        });
        _standardProvider = standardServices.BuildServiceProvider();
        // InMemoryCacheManager implements IMemoryCache
        var cacheManager = _standardProvider.GetRequiredService<ICacheManager>();
        _standardMemory = (IMemoryCache)cacheManager;

        // Advanced Memory Provider with LRU (from MethodCache.Providers.Memory)
        var advancedLruServices = new ServiceCollection();
        advancedLruServices.AddLogging();
        advancedLruServices.AddMethodCache(config =>
        {
            config.DefaultPolicy(builder => builder.WithDuration(TimeSpan.FromMinutes(10)));
        });
        advancedLruServices.AddAdvancedMemoryStorage(opts =>
        {
            opts.EvictionPolicy = EvictionPolicy.LRU;
            opts.MaxEntries = 10000;
            opts.EnableDetailedStats = false;
        });
        _advancedLruProvider = advancedLruServices.BuildServiceProvider();
        _advancedMemoryLru = _advancedLruProvider.GetRequiredService<IMemoryStorage>();

        // Advanced Memory Provider with LFU
        var advancedLfuServices = new ServiceCollection();
        advancedLfuServices.AddLogging();
        advancedLfuServices.AddMethodCache(config =>
        {
            config.DefaultPolicy(builder => builder.WithDuration(TimeSpan.FromMinutes(10)));
        });
        advancedLfuServices.AddAdvancedMemoryStorage(opts =>
        {
            opts.EvictionPolicy = EvictionPolicy.LFU;
            opts.MaxEntries = 10000;
            opts.EnableDetailedStats = false;
        });
        _advancedLfuProvider = advancedLfuServices.BuildServiceProvider();
        _advancedMemoryLfu = _advancedLfuProvider.GetRequiredService<IMemoryStorage>();

        // Warm up all caches
        WarmupCaches().GetAwaiter().GetResult();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        WarmupCaches().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _standardMemory?.Dispose();
        (_standardProvider as IDisposable)?.Dispose();
        (_advancedLruProvider as IDisposable)?.Dispose();
        (_advancedLfuProvider as IDisposable)?.Dispose();
    }

    private async Task WarmupCaches()
    {
        var duration = TimeSpan.FromMinutes(10);
        await _standardMemory.SetAsync(TestKey, TestPayload, duration);
        await _advancedMemoryLru.SetAsync(TestKey, TestPayload, duration);
        await _advancedMemoryLfu.SetAsync(TestKey, TestPayload, duration);
    }

    // ==================== CACHE HIT TESTS (Async) ====================

    [BenchmarkCategory("CacheHit"), Benchmark(Baseline = true)]
    public async Task<TestPayload?> Standard_HitAsync()
    {
        return await _standardMemory.GetAsync<TestPayload>(TestKey);
    }

    [BenchmarkCategory("CacheHit"), Benchmark]
    public async Task<TestPayload?> Advanced_LRU_HitAsync()
    {
        return await _advancedMemoryLru.GetAsync<TestPayload>(TestKey);
    }

    [BenchmarkCategory("CacheHit"), Benchmark]
    public async Task<TestPayload?> Advanced_LFU_HitAsync()
    {
        return await _advancedMemoryLfu.GetAsync<TestPayload>(TestKey);
    }

    // ==================== CACHE SET TESTS ====================

    [BenchmarkCategory("CacheSet"), Benchmark(Baseline = true)]
    public async Task Standard_SetAsync()
    {
        var key = $"{TestKey}_set_{Guid.NewGuid()}";
        await _standardMemory.SetAsync(key, TestPayload, TimeSpan.FromMinutes(10));
    }

    [BenchmarkCategory("CacheSet"), Benchmark]
    public async Task Advanced_LRU_SetAsync()
    {
        var key = $"{TestKey}_set_{Guid.NewGuid()}";
        await _advancedMemoryLru.SetAsync(key, TestPayload, TimeSpan.FromMinutes(10));
    }

    [BenchmarkCategory("CacheSet"), Benchmark]
    public async Task Advanced_LFU_SetAsync()
    {
        var key = $"{TestKey}_set_{Guid.NewGuid()}";
        await _advancedMemoryLfu.SetAsync(key, TestPayload, TimeSpan.FromMinutes(10));
    }

    // ==================== CONCURRENT ACCESS TESTS (Standard) ====================

    [BenchmarkCategory("Concurrent"), Benchmark(Baseline = true)]
    [Arguments(10)]
    [Arguments(100)]
    public async Task Standard_ConcurrentHits(int threadCount)
    {
        // Pre-populate cache with 10 hot keys
        var hotKeys = new string[10];
        for (int i = 0; i < 10; i++)
        {
            hotKeys[i] = $"{TestKey}_hot_std_{i}";
            await _standardMemory.SetAsync(hotKeys[i], TestPayload, TimeSpan.FromMinutes(10));
        }

        var tasks = new Task[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            int threadId = i;
            tasks[i] = Task.Run(async () =>
            {
                for (int j = 0; j < 256; j++)
                {
                    var key = hotKeys[(threadId + j) % hotKeys.Length];
                    await _standardMemory.GetAsync<TestPayload>(key);
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    // ==================== CONCURRENT ACCESS TESTS (Advanced LRU) ====================

    [BenchmarkCategory("Concurrent"), Benchmark]
    [Arguments(10)]
    [Arguments(100)]
    public async Task Advanced_LRU_ConcurrentHits(int threadCount)
    {
        // Pre-populate cache with 10 hot keys
        var hotKeys = new string[10];
        for (int i = 0; i < 10; i++)
        {
            hotKeys[i] = $"{TestKey}_hot_lru_{i}";
            _advancedMemoryLru.Set(hotKeys[i], TestPayload, TimeSpan.FromMinutes(10));
        }

        var tasks = new Task[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            int threadId = i;
            tasks[i] = Task.Run(async () =>
            {
                for (int j = 0; j < 256; j++)
                {
                    var key = hotKeys[(threadId + j) % hotKeys.Length];
                    await _advancedMemoryLru.GetAsync<TestPayload>(key);
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    // ==================== CONCURRENT ACCESS TESTS (Advanced LFU) ====================

    [BenchmarkCategory("Concurrent"), Benchmark]
    [Arguments(10)]
    [Arguments(100)]
    public async Task Advanced_LFU_ConcurrentHits(int threadCount)
    {
        // Pre-populate cache with 10 hot keys
        var hotKeys = new string[10];
        for (int i = 0; i < 10; i++)
        {
            hotKeys[i] = $"{TestKey}_hot_lfu_{i}";
            _advancedMemoryLfu.Set(hotKeys[i], TestPayload, TimeSpan.FromMinutes(10));
        }

        var tasks = new Task[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            int threadId = i;
            tasks[i] = Task.Run(async () =>
            {
                for (int j = 0; j < 256; j++)
                {
                    var key = hotKeys[(threadId + j) % hotKeys.Length];
                    await _advancedMemoryLfu.GetAsync<TestPayload>(key);
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    // ==================== MIXED READ/WRITE TESTS ====================

    [BenchmarkCategory("MixedWorkload"), Benchmark(Baseline = true)]
    public async Task Standard_MixedWorkload()
    {
        // Simulate realistic workload: 80% reads, 20% writes
        for (int i = 0; i < 100; i++)
        {
            if (i % 5 == 0)
            {
                // Write
                var key = $"{TestKey}_mixed_std_{i}";
                await _standardMemory.SetAsync(key, TestPayload, TimeSpan.FromMinutes(10));
            }
            else
            {
                // Read
                await _standardMemory.GetAsync<TestPayload>(TestKey);
            }
        }
    }

    [BenchmarkCategory("MixedWorkload"), Benchmark]
    public async Task Advanced_LRU_MixedWorkload()
    {
        // Simulate realistic workload: 80% reads, 20% writes
        for (int i = 0; i < 100; i++)
        {
            if (i % 5 == 0)
            {
                // Write
                var key = $"{TestKey}_mixed_lru_{i}";
                await _advancedMemoryLru.SetAsync(key, TestPayload, TimeSpan.FromMinutes(10));
            }
            else
            {
                // Read
                await _advancedMemoryLru.GetAsync<TestPayload>(TestKey);
            }
        }
    }

    [BenchmarkCategory("MixedWorkload"), Benchmark]
    public async Task Advanced_LFU_MixedWorkload()
    {
        // Simulate realistic workload: 80% reads, 20% writes
        for (int i = 0; i < 100; i++)
        {
            if (i % 5 == 0)
            {
                // Write
                var key = $"{TestKey}_mixed_lfu_{i}";
                await _advancedMemoryLfu.SetAsync(key, TestPayload, TimeSpan.FromMinutes(10));
            }
            else
            {
                // Read
                await _advancedMemoryLfu.GetAsync<TestPayload>(TestKey);
            }
        }
    }
}

/// <summary>
/// Test payload for memory provider benchmarks
/// </summary>
public class TestPayload
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte[] Data { get; set; } = Array.Empty<byte>();
}
