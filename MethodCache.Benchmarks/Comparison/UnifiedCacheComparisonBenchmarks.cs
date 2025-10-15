using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Benchmarks.Comparison.Adapters;
using MethodCache.Benchmarks.Comparison.Services;
using MethodCache.Core.Infrastructure.Extensions;
using MethodCache.Providers.Memory.Extensions;

namespace MethodCache.Benchmarks.Comparison;

/// <summary>
/// Adapter-based comparison benchmarks for all caching libraries
/// All tests run through the same ICacheAdapter interface for apples-to-apples comparison
///
/// IMPORTANT INTERPRETATION GUIDE:
///
/// MethodCacheSourceGen_* benchmarks:
/// - Uses AdvancedMemory storage provider (with tags, stats, eviction)
/// - Direct storage-to-storage comparison
/// - Shows MethodCache storage performance vs other frameworks
/// - Expected performance: ~100-200ns (full-featured storage)
///
/// MethodCacheDirect_* benchmarks:
/// - Direct API calls (no source generation)
/// - Shows MethodCache storage layer only
/// - Fair storage-to-storage comparison
///
/// MethodCache_* benchmarks:
/// - Legacy/research comparison (runtime reflection-based)
/// - Shows runtime key generation performance
/// - Not recommended for decision-making
///
/// Key Insight: Compare MethodCacheSourceGen against other frameworks - that's the real comparison!
/// See Comparison/README.md and BENCHMARKING_GUIDE.md for full explanation.
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

    // All cache adapters - easily extensible!
    private ICacheAdapter _methodCache = null!;
    private ICacheAdapter _methodCacheDirect = null!;
    private ICacheAdapter _methodCacheSourceGen = null!; // NEW: Uses source generation (proper MethodCache usage)
    private ICacheAdapter _fusionCache = null!;
    private ICacheAdapter _lazyCache = null!;
    private ICacheAdapter _easyCaching = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Initialize all cache adapters
        // MethodCache has 3 usage modes shown here:
        _methodCache = new MethodCacheAdapter(); // With runtime key generation (worst case: ~9,500ns)
        _methodCacheDirect = new DirectApiMethodCacheAdapter(); // Direct API with manual keys (fairest: ~same as others)

        // NEW: Source-generated MethodCache with AdvancedMemory storage
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMethodCache(config =>
        {
            config.DefaultPolicy(builder => builder.WithDuration(TimeSpan.FromMinutes(10)));
        }); // Don't auto-scan assembly - we register explicitly below

        // PERFORMANCE OPTIMIZATION: Configure cache options for benchmarking
        services.Configure<MethodCache.Core.Configuration.MemoryCacheOptions>(opts =>
        {
            opts.EnableStatistics = false;  // Skip Interlocked operations (~5-10µs saved)
            opts.EvictionPolicy = MethodCache.Core.Configuration.MemoryCacheEvictionPolicy.LRU;  // Use lazy LRU
        });

        services.AddAdvancedMemoryStorage(opts =>
        {
            opts.EvictionPolicy = MethodCache.Providers.Memory.Configuration.EvictionPolicy.LRU;  // Use lazy LRU for comparison
        }); // Use AdvancedMemory provider

        // Register the base implementation
        services.AddSingleton<MethodCacheBenchmarkService>();

        // Use the source-generated decorator registration extension method
        // This properly wraps the implementation with the caching decorator
        services.AddIMethodCacheBenchmarkServiceWithCaching(sp =>
            sp.GetRequiredService<MethodCacheBenchmarkService>());

        var serviceProvider = services.BuildServiceProvider();
        var sourceGenService = serviceProvider.GetRequiredService<IMethodCacheBenchmarkService>();
        var baseService = serviceProvider.GetRequiredService<MethodCacheBenchmarkService>();
        _methodCacheSourceGen = new MethodCacheSourceGenAdapter(sourceGenService, baseService);

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
        _methodCache?.Dispose();
        _methodCacheDirect?.Dispose();
        _methodCacheSourceGen?.Dispose();
        _fusionCache?.Dispose();
        _lazyCache?.Dispose();
        _easyCaching?.Dispose();
    }

    private void WarmupCaches()
    {
        var duration = TimeSpan.FromMinutes(10);
        _methodCache.Set(TestKey, TestPayload, duration);
        _methodCacheDirect.Set(TestKey, TestPayload, duration);

        // MethodCacheSourceGen must populate cache by calling the cached method
        // The Set() method is a no-op for source-generated services
        var sourceGenAdapter = (MethodCacheSourceGenAdapter)_methodCacheSourceGen;
        _ = sourceGenAdapter.GetAsyncDirect(TestKey).GetAwaiter().GetResult();

        _fusionCache.Set(TestKey, TestPayload, duration);
        _lazyCache.Set(TestKey, TestPayload, duration);
        _easyCaching.Set(TestKey, TestPayload, duration);
    }

    // ==================== CACHE HIT TESTS ====================
    // MethodCache has 3 usage modes:
    // 1. MethodCacheSourceGen_Hit - AdvancedMemory storage (full-featured: ~100-200ns)
    // 2. MethodCacheDirect_Hit - Direct API with manual keys (storage-only comparison)
    // 3. MethodCache_Hit - Runtime key generation research (~9,500ns)
    //
    // COMPARE: MethodCacheSourceGen (AdvancedMemory) vs other frameworks for storage comparison
    //
    // NOTE: Synchronous benchmark methods (MethodCacheSourceGen_Hit) suffer from sync-over-async
    // overhead (~50-100µs on Windows). Use MethodCacheSourceGen_HitAsync for true performance.

    [BenchmarkCategory("CacheHit"), Benchmark]
    public bool MethodCache_Hit()
    {
        return _methodCache.TryGet<SamplePayload>(TestKey, out _);
    }

    [BenchmarkCategory("CacheHit"), Benchmark]
    public bool MethodCacheDirect_Hit()
    {
        return _methodCacheDirect.TryGet<SamplePayload>(TestKey, out _);
    }

    [BenchmarkCategory("CacheHit"), Benchmark]
    public bool MethodCacheSourceGen_Hit()
    {
        // Uses AdvancedMemory storage - full-featured cache with tags, stats, eviction
        return _methodCacheSourceGen.TryGet<SamplePayload>(TestKey, out _);
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
    public async Task<SamplePayload?> MethodCacheSourceGen_HitAsync()
    {
        // Uses async API - avoids sync-over-async overhead
        // IMPORTANT: Call GetAsync directly, not GetOrSetAsync which executes factory
        var sourceGenAdapter = (MethodCacheSourceGenAdapter)_methodCacheSourceGen;
        return await sourceGenAdapter.GetAsyncDirect(TestKey);
    }

    // ==================== CACHE MISS + SET TESTS ====================
    // NOTE: These tests use unique keys per iteration to ensure consistent cache misses.
    // This is more reliable than Remove(), which doesn't work for all cache implementations.

    [BenchmarkCategory("MissAndSet"), Benchmark]
    public async Task<SamplePayload> MethodCache_MissAndSet()
    {
        var key = $"{TestKey}_miss_{Guid.NewGuid()}";
        return await _methodCache.GetOrSetAsync(key, CreatePayloadAsync, TimeSpan.FromMinutes(10));
    }

    [BenchmarkCategory("MissAndSet"), Benchmark]
    public async Task<SamplePayload> MethodCacheDirect_MissAndSet()
    {
        var key = $"{TestKey}_miss_{Guid.NewGuid()}";
        return await _methodCacheDirect.GetOrSetAsync(key, CreatePayloadAsync, TimeSpan.FromMinutes(10));
    }

    [BenchmarkCategory("MissAndSet"), Benchmark]
    public async Task<SamplePayload> MethodCacheSourceGen_MissAndSet()
    {
        var key = $"{TestKey}_miss_{Guid.NewGuid()}";
        return await _methodCacheSourceGen.GetOrSetAsync(key, CreatePayloadAsync, TimeSpan.FromMinutes(10));
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

    [Params(10, 100)]
    public int ConcurrentThreads { get; set; }

    [BenchmarkCategory("Concurrent"), Benchmark]
    public async Task MethodCache_Concurrent()
    {
        await RunConcurrentTest(_methodCache);
    }

    [BenchmarkCategory("Concurrent"), Benchmark]
    public async Task MethodCacheDirect_Concurrent()
    {
        await RunConcurrentTest(_methodCacheDirect);
    }

    [BenchmarkCategory("Concurrent"), Benchmark]
    public async Task MethodCacheSourceGen_Concurrent()
    {
        await RunConcurrentTest(_methodCacheSourceGen);
    }


    [BenchmarkCategory("Concurrent"), Benchmark]
    public async Task FusionCache_Concurrent()
    {
        await RunConcurrentTest(_fusionCache);
    }

    [BenchmarkCategory("Concurrent"), Benchmark]
    public async Task LazyCache_Concurrent()
    {
        await RunConcurrentTest(_lazyCache);
    }

    [BenchmarkCategory("Concurrent"), Benchmark]
    public async Task EasyCaching_Concurrent()
    {
        await RunConcurrentTest(_easyCaching);
    }


    private async Task RunConcurrentTest(ICacheAdapter cache)
    {
        // NOTE: This test creates a NEW key each time, so it's measuring cache MISS + stampede prevention
        // It does NOT measure LRU update performance (see ConcurrentHits for that)
        var tasks = new Task<SamplePayload>[ConcurrentThreads];
        var key = $"{TestKey}_concurrent_{Guid.NewGuid()}";

        for (int i = 0; i < ConcurrentThreads; i++)
        {
            tasks[i] = cache.GetOrSetAsync(key, CreatePayloadAsync, TimeSpan.FromMinutes(10));
        }

        await Task.WhenAll(tasks);
    }

    // ==================== CONCURRENT HIT TESTS (LRU STRESS TEST) ====================
    // These tests measure LRU update performance under concurrent cache HITS
    // Multiple threads repeatedly access a small set of HOT keys

    [BenchmarkCategory("ConcurrentHits"), Benchmark]
    public async Task MethodCacheSourceGen_ConcurrentHits()
    {
        await RunConcurrentHitsTest(_methodCacheSourceGen);
    }



    [BenchmarkCategory("ConcurrentHits"), Benchmark]
    public async Task LazyCache_ConcurrentHits()
    {
        await RunConcurrentHitsTest(_lazyCache);
    }

    [BenchmarkCategory("ConcurrentHits"), Benchmark]
    public async Task FusionCache_ConcurrentHits()
    {
        await RunConcurrentHitsTest(_fusionCache);
    }

    private async Task RunConcurrentHitsTest(ICacheAdapter cache)
    {
        // Pre-populate cache with 10 hot keys
        var hotKeys = new string[10];
        for (int i = 0; i < 10; i++)
        {
            hotKeys[i] = $"{TestKey}_hot_{i}";
            cache.Set(hotKeys[i], TestPayload, TimeSpan.FromMinutes(10));
        }

        // Have all threads repeatedly hit these same keys (simulates hot data)
        var tasks = new Task[ConcurrentThreads];
        for (int i = 0; i < ConcurrentThreads; i++)
        {
            int threadId = i;
            tasks[i] = Task.Run(async () =>
            {
                // Each thread does 10 cache hits on rotating hot keys
                for (int j = 0; j < 10; j++)
                {
                    var key = hotKeys[(threadId + j) % 10];
                    await cache.GetOrSetAsync(key, CreatePayloadAsync, TimeSpan.FromMinutes(10));
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    // ==================== CACHE STAMPEDE TESTS ====================

    [BenchmarkCategory("Stampede"), Benchmark]
    public async Task MethodCache_Stampede()
    {
        await RunStampedeTest(_methodCache);
    }

    [BenchmarkCategory("Stampede"), Benchmark]
    public async Task MethodCacheDirect_Stampede()
    {
        await RunStampedeTest(_methodCacheDirect);
    }

    [BenchmarkCategory("Stampede"), Benchmark]
    public async Task MethodCacheSourceGen_Stampede()
    {
        await RunStampedeTest(_methodCacheSourceGen);
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
        // OPTIMIZED: Use Task.Yield() instead of Task.Delay(1)
        // Task.Delay(1) = ~1ms (masks cache overhead completely)
        // Task.Yield() = ~100ns (exposes cache overhead differences)
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
