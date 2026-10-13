using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using MethodCache.Benchmarks.Comparison.Adapters;
using MethodCache.Benchmarks.Comparison.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Benchmarks.Comparison;

/// <summary>
/// Compares cache operations through <see cref="ICacheAdapter"/>. MethodCache rows exercise a generated
/// decorator, manual storage keys, or runtime key generation. These paths perform different work;
/// use ReadmeComparisonBenchmarks for the documented application-level comparison.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class UnifiedCacheComparisonBenchmarks
{
    private const string TestKey = "benchmark_key";
    private static readonly SamplePayload TestPayload = new() { Id = 1, Name = "Test", Data = new byte[1024] };
    private const int ConcurrentHitIterationsPerThread = 256;

    // All cache adapters - easily extensible!
    private ICacheAdapter _runtimeKeyMethodCache = null!;
    private ICacheAdapter _manualKeyMethodCache = null!;
    private ICacheAdapter _sourceGenMethodCache = null!;
    private ICacheAdapter _fusionCache = null!;
    private ICacheAdapter _lazyCache = null!;
    private ICacheAdapter _easyCaching = null!;

    private IDisposable? _sourceGenProvider;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Initialize all cache adapters
        // MethodCache has 3 usage modes shown here:
        _runtimeKeyMethodCache = new RuntimeKeyMethodCacheAdapter(); // Runtime key generation
        _manualKeyMethodCache = new DirectApiMethodCacheAdapter(); // Direct API with manual keys (raw storage comparison)

        // Source-generated MethodCache with the default in-memory engine
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMethodCache(config =>
        {
            config.DefaultPolicy(builder => builder.WithDuration(TimeSpan.FromMinutes(10)));
        }); // Don't auto-scan assembly - we register explicitly below

        // PERFORMANCE OPTIMIZATION: Configure cache options for benchmarking
        services.Configure<MethodCache.MethodCacheMemoryOptions>(opts =>
        {
            opts.EnableStatistics = false;
            opts.EvictionPolicy = MethodCache.MemoryCacheEvictionPolicy.Lru;
        });


        // Register the base implementation
        services.AddSingleton<MethodCacheBenchmarkService>();

        // Use the source-generated decorator registration extension method
        // This properly wraps the implementation with the caching decorator
        services.AddIMethodCacheBenchmarkServiceWithCachingSingleton(sp =>
            sp.GetRequiredService<MethodCacheBenchmarkService>());

        var serviceProvider = services.BuildServiceProvider();
        _sourceGenProvider = serviceProvider;
        var sourceGenService = serviceProvider.GetRequiredService<IMethodCacheBenchmarkService>();
        _sourceGenMethodCache = new MethodCacheSourceGenAdapter(sourceGenService, serviceProvider.GetRequiredService<ICacheManager>());

        _fusionCache = new FusionCacheAdapter();
        _lazyCache = new LazyCacheAdapter();
        _easyCaching = new EasyCachingAdapter();

        // Initial warmup
        WarmupCaches();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Ensure cache is warmed up before each iteration
        // This is critical for CacheHit benchmarks to measure actual hits, not misses!
        WarmupCaches();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _runtimeKeyMethodCache?.Dispose();
        _manualKeyMethodCache?.Dispose();
        _sourceGenMethodCache?.Dispose();
        _sourceGenProvider?.Dispose();
        _fusionCache?.Dispose();
        _lazyCache?.Dispose();
        _easyCaching?.Dispose();
    }

    private void WarmupCaches()
    {
        var duration = TimeSpan.FromMinutes(10);
        _runtimeKeyMethodCache.Set(TestKey, TestPayload, duration);
        _manualKeyMethodCache.Set(TestKey, TestPayload, duration);

        // MethodCache_SourceGen must populate cache by calling the cached method so the decorator path runs
        var sourceGenAdapter = (MethodCacheSourceGenAdapter)_sourceGenMethodCache;
        _ = sourceGenAdapter.GetAsyncDirect(TestKey).GetAwaiter().GetResult();
        sourceGenAdapter.TryGet<SamplePayload>(TestKey, out _);

        _fusionCache.Set(TestKey, TestPayload, duration);
        _lazyCache.Set(TestKey, TestPayload, duration);
        _easyCaching.Set(TestKey, TestPayload, duration);
    }

    // ==================== CACHE HIT TESTS ====================
    // SourceGen calls the generated decorator, ManualKey reads storage directly,
    // and RuntimeKey generates a key through ICacheKeyGenerator on each operation.

    [BenchmarkCategory("CacheHit"), Benchmark]
    public bool MethodCache_RuntimeKey_Hit()
    {
        return _runtimeKeyMethodCache.TryGet<SamplePayload>(TestKey, out _);
    }

    [BenchmarkCategory("CacheHit"), Benchmark]
    public bool MethodCache_ManualKey_Hit()
    {
        return _manualKeyMethodCache.TryGet<SamplePayload>(TestKey, out _);
    }

    [BenchmarkCategory("CacheHit"), Benchmark]
    public bool MethodCache_SourceGen_Hit()
    {
        // Uses the default in-memory engine - full-featured cache with tags, stats, eviction
        return _sourceGenMethodCache.TryGet<SamplePayload>(TestKey, out _);
    }


    [BenchmarkCategory("CacheHit"), Benchmark]
    public bool FusionCache_Hit()
    {
        return _fusionCache.TryGet<SamplePayload>(TestKey, out _);
    }

    [BenchmarkCategory("CacheHit"), Benchmark]
    public bool LazyCache_Hit()
    {
        return _lazyCache.TryGet<SamplePayload>(TestKey, out _);
    }

    [BenchmarkCategory("CacheHit"), Benchmark]
    public bool EasyCaching_Hit()
    {
        return _easyCaching.TryGet<SamplePayload>(TestKey, out _);
    }


    // ==================== ASYNC CACHE HIT TESTS (OPTIMIZED) ====================
    // These async tests avoid the sync-over-async overhead and show true performance

    [BenchmarkCategory("CacheHitAsync"), Benchmark]
    public async Task<SamplePayload?> MethodCache_SourceGen_HitAsync()
    {
        // Uses async API - avoids sync-over-async overhead
        // IMPORTANT: Call GetAsync directly, not GetOrSetAsync which executes factory
        var sourceGenAdapter = (MethodCacheSourceGenAdapter)_sourceGenMethodCache;
        return await sourceGenAdapter.GetAsyncDirect(TestKey);
    }

    // ==================== CACHE MISS + SET TESTS ====================
    // NOTE: These tests use unique keys per iteration to ensure consistent cache misses.

    [BenchmarkCategory("MissAndSet"), Benchmark]
    public async Task<SamplePayload> MethodCache_RuntimeKey_MissAndSet()
    {
        var key = $"{TestKey}_miss_{Guid.NewGuid()}";
        return await _runtimeKeyMethodCache.GetOrSetAsync(key, CreatePayloadAsync, TimeSpan.FromMinutes(10));
    }

    [BenchmarkCategory("MissAndSet"), Benchmark]
    public async Task<SamplePayload> MethodCache_ManualKey_MissAndSet()
    {
        var key = $"{TestKey}_miss_{Guid.NewGuid()}";
        return await _manualKeyMethodCache.GetOrSetAsync(key, CreatePayloadAsync, TimeSpan.FromMinutes(10));
    }

    [BenchmarkCategory("MissAndSet"), Benchmark]
    public async Task<SamplePayload> MethodCache_SourceGen_MissAndSet()
    {
        var key = $"{TestKey}_miss_{Guid.NewGuid()}";
        return await _sourceGenMethodCache.GetOrSetAsync(key, CreatePayloadAsync, TimeSpan.FromMinutes(10));
    }


    [BenchmarkCategory("MissAndSet"), Benchmark]
    public async Task<SamplePayload> FusionCache_MissAndSet()
    {
        var key = $"{TestKey}_miss_{Guid.NewGuid()}";
        return await _fusionCache.GetOrSetAsync(key, CreatePayloadAsync, TimeSpan.FromMinutes(10));
    }

    [BenchmarkCategory("MissAndSet"), Benchmark]
    public async Task<SamplePayload> LazyCache_MissAndSet()
    {
        var key = $"{TestKey}_miss_{Guid.NewGuid()}";
        return await _lazyCache.GetOrSetAsync(key, CreatePayloadAsync, TimeSpan.FromMinutes(10));
    }

    [BenchmarkCategory("MissAndSet"), Benchmark]
    public async Task<SamplePayload> EasyCaching_MissAndSet()
    {
        var key = $"{TestKey}_miss_{Guid.NewGuid()}";
        return await _easyCaching.GetOrSetAsync(key, CreatePayloadAsync, TimeSpan.FromMinutes(10));
    }


    // ==================== BENCHMARK PARAMETERS ====================

    [BenchmarkCategory("Concurrent"), Benchmark]
    [Arguments(10)]
    [Arguments(100)]
    public async Task MethodCache_RuntimeKey_Concurrent(int threadCount)
    {
        await RunConcurrentTest(_runtimeKeyMethodCache, threadCount);
    }

    [BenchmarkCategory("Concurrent"), Benchmark]
    [Arguments(10)]
    [Arguments(100)]
    public async Task MethodCache_ManualKey_Concurrent(int threadCount)
    {
        await RunConcurrentTest(_manualKeyMethodCache, threadCount);
    }

    [BenchmarkCategory("Concurrent"), Benchmark]
    [Arguments(10)]
    [Arguments(100)]
    public async Task MethodCache_SourceGen_Concurrent(int threadCount)
    {
        await RunConcurrentTest(_sourceGenMethodCache, threadCount);
    }


    [BenchmarkCategory("Concurrent"), Benchmark]
    [Arguments(10)]
    [Arguments(100)]
    public async Task FusionCache_Concurrent(int threadCount)
    {
        await RunConcurrentTest(_fusionCache, threadCount);
    }

    [BenchmarkCategory("Concurrent"), Benchmark]
    [Arguments(10)]
    [Arguments(100)]
    public async Task LazyCache_Concurrent(int threadCount)
    {
        await RunConcurrentTest(_lazyCache, threadCount);
    }

    [BenchmarkCategory("Concurrent"), Benchmark]
    [Arguments(10)]
    [Arguments(100)]
    public async Task EasyCaching_Concurrent(int threadCount)
    {
        await RunConcurrentTest(_easyCaching, threadCount);
    }


    private async Task RunConcurrentTest(ICacheAdapter cache, int threadCount)
    {
        // NOTE: This test creates a NEW key each time, so it's measuring cache MISS + stampede prevention
        // It does NOT measure LRU update performance (see ConcurrentHits for that)
        var tasks = new Task<SamplePayload>[threadCount];
        var key = $"{TestKey}_concurrent_{Guid.NewGuid()}";

        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = cache.GetOrSetAsync(key, CreatePayloadAsync, TimeSpan.FromMinutes(10));
        }

        await Task.WhenAll(tasks);
    }

    // ==================== CONCURRENT HIT TESTS (LRU STRESS TEST) ====================
    // These tests measure LRU update performance under concurrent cache HITS
    // Multiple threads repeatedly access a small set of HOT keys

    [BenchmarkCategory("ConcurrentHits"), Benchmark]
    [Arguments(10)]
    [Arguments(100)]
    public async Task MethodCache_SourceGen_ConcurrentHits(int threadCount)
    {
        await RunConcurrentHitsTest(_sourceGenMethodCache, threadCount);
    }



    [BenchmarkCategory("ConcurrentHits"), Benchmark]
    [Arguments(10)]
    [Arguments(100)]
    public async Task LazyCache_ConcurrentHits(int threadCount)
    {
        await RunConcurrentHitsTest(_lazyCache, threadCount);
    }

    [BenchmarkCategory("ConcurrentHits"), Benchmark]
    [Arguments(10)]
    [Arguments(100)]
    public async Task FusionCache_ConcurrentHits(int threadCount)
    {
        await RunConcurrentHitsTest(_fusionCache, threadCount);
    }

    private async Task RunConcurrentHitsTest(ICacheAdapter cache, int threadCount)
    {
        // Pre-populate cache with 10 hot keys
        var hotKeys = new string[10];
        for (int i = 0; i < 10; i++)
        {
            hotKeys[i] = $"{TestKey}_hot_{i}";
            cache.Set(hotKeys[i], TestPayload, TimeSpan.FromMinutes(10));
        }

        // Have all threads repeatedly hit these same keys (simulates hot data)
        var tasks = new Task[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            int threadId = i;
            tasks[i] = Task.Run(async () =>
            {
                // Each thread performs a large number of hits to ensure BenchmarkDotNet meets its minimum iteration time guidance.
                for (int j = 0; j < ConcurrentHitIterationsPerThread; j++)
                {
                    var key = hotKeys[(threadId + j) % hotKeys.Length];
                    await cache.GetOrSetAsync(key, CreatePayloadAsync, TimeSpan.FromMinutes(10));
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    // ==================== CACHE STAMPEDE TESTS ====================

    [BenchmarkCategory("Stampede"), Benchmark]
    public async Task MethodCache_RuntimeKey_Stampede()
    {
        await RunStampedeTest(_runtimeKeyMethodCache);
    }

    [BenchmarkCategory("Stampede"), Benchmark]
    public async Task MethodCache_ManualKey_Stampede()
    {
        await RunStampedeTest(_manualKeyMethodCache);
    }

    [BenchmarkCategory("Stampede"), Benchmark]
    public async Task MethodCache_SourceGen_Stampede()
    {
        await RunStampedeTest(_sourceGenMethodCache);
    }


    [BenchmarkCategory("Stampede"), Benchmark]
    public async Task FusionCache_Stampede()
    {
        await RunStampedeTest(_fusionCache);
    }

    [BenchmarkCategory("Stampede"), Benchmark]
    public async Task LazyCache_Stampede()
    {
        await RunStampedeTest(_lazyCache);
    }

    [BenchmarkCategory("Stampede"), Benchmark]
    public async Task EasyCaching_Stampede()
    {
        await RunStampedeTest(_easyCaching);
    }


    private async Task RunStampedeTest(ICacheAdapter cache)
    {
        var key = $"{TestKey}_stampede_{Guid.NewGuid()}";
        cache.Remove(key); // Ensure miss

        // Simulate stampede - many concurrent requests for the same missing key
        var tasks = new Task<SamplePayload>[50];
        for (int i = 0; i < 50; i++)
        {
            tasks[i] = cache.GetOrSetAsync(key, CreateSlowPayloadAsync, TimeSpan.FromMinutes(10));
        }

        await Task.WhenAll(tasks);
    }

    // ==================== HELPER METHODS ====================

    private static async Task<SamplePayload> CreatePayloadAsync()
    {
        // NOTE: We intentionally use Task.Yield() instead of Task.Delay to expose cache overhead
        // rather than simulate real-world latency. See README for how to reintroduce realistic work.
        // OPTIMIZED: Use Task.Yield() instead of Task.Delay(1)
        // Task.Delay(1) = ~1ms (masks cache overhead completely)
        // Yield so concurrent misses can overlap.
        await Task.Yield();
        return new SamplePayload { Id = 1, Name = "Generated", Data = new byte[1024] };
    }

    private static async Task<SamplePayload> CreateSlowPayloadAsync()
    {
        // OPTIMIZED: Use Task.Yield() to force async without adding significant time
        // This hides factory duration so we can measure stampede coordination overhead
        await Task.Yield();
        return new SamplePayload { Id = 1, Name = "Generated", Data = new byte[1024] };
    }
}

/// <summary>
/// Sample payload for benchmarks
/// </summary>
public class SamplePayload
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte[] Data { get; set; } = Array.Empty<byte>();
}
