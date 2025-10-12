using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using MethodCache.Benchmarks.Comparison.Adapters;

namespace MethodCache.Benchmarks.Comparison;

/// <summary>
/// Adapter-based comparison benchmarks for all caching libraries
/// All tests run through the same ICacheAdapter interface for normalized comparison
///
/// IMPORTANT: This adds ~700ns overhead to MethodCache due to generic key generation.
/// For MethodCache's real performance (15-58 ns), see RealMethodCacheComparison.cs
///
/// Use these results for:
/// - Comparing stampede prevention across frameworks
/// - Testing concurrent access patterns
/// - Understanding normalized feature implementations
///
/// DON'T use these results to claim MethodCache is slower than other frameworks!
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
    private ICacheAdapter _methodCacheStatic = null!;
    private ICacheAdapter _methodCacheDirect = null!;
    private ICacheAdapter _fusionCache = null!;
    private ICacheAdapter _lazyCache = null!;
    private ICacheAdapter _memoryCache = null!;
    private ICacheAdapter _easyCaching = null!;
    private ICacheAdapter _fastCache = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Initialize all cache adapters
        // MethodCache has 3 usage modes shown here:
        _methodCache = new MethodCacheAdapter(); // With runtime key generation (worst case: ~9,500ns)
        _methodCacheStatic = new StaticKeyMethodCacheAdapter(); // With pre-generated keys (like source gen: ~2,200ns)
        _methodCacheDirect = new DirectApiMethodCacheAdapter(); // Direct API with manual keys (fairest: ~same as others)

        _fusionCache = new FusionCacheAdapter();
        _lazyCache = new LazyCacheAdapter();
        _memoryCache = new MemoryCacheAdapter();
        _easyCaching = new EasyCachingAdapter();
        _fastCache = new FastCacheAdapter();

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
        _methodCacheStatic?.Dispose();
        _methodCacheDirect?.Dispose();
        _fusionCache?.Dispose();
        _lazyCache?.Dispose();
        _memoryCache?.Dispose();
        _easyCaching?.Dispose();
        _fastCache?.Dispose();
    }

    private void WarmupCaches()
    {
        var duration = TimeSpan.FromMinutes(10);
        _methodCache.Set(TestKey, TestPayload, duration);
        _methodCacheStatic.Set(TestKey, TestPayload, duration);
        _methodCacheDirect.Set(TestKey, TestPayload, duration);
        _fusionCache.Set(TestKey, TestPayload, duration);
        _lazyCache.Set(TestKey, TestPayload, duration);
        _memoryCache.Set(TestKey, TestPayload, duration);
        _easyCaching.Set(TestKey, TestPayload, duration);
        _fastCache.Set(TestKey, TestPayload, duration);
    }

    // ==================== CACHE HIT TESTS ====================
    // MethodCache has 3 usage modes:
    // 1. MethodCache_Hit - Runtime key generation (shows overhead: ~9,500ns)
    // 2. MethodCacheStatic_Hit - Pre-generated keys like source gen (~2,200ns)
    // 3. MethodCacheDirect_Hit - Direct API with manual keys (fairest comparison)
    // For true source-generated performance, see RealMethodCacheComparison.cs (15-58ns)

    [BenchmarkCategory("CacheHit"), Benchmark]
    public bool MethodCache_Hit()
    {
        return _methodCache.TryGet<SamplePayload>(TestKey, out _);
    }

    [BenchmarkCategory("CacheHit"), Benchmark]
    public bool MethodCacheStatic_Hit()
    {
        return _methodCacheStatic.TryGet<SamplePayload>(TestKey, out _);
    }

    [BenchmarkCategory("CacheHit"), Benchmark]
    public bool MethodCacheDirect_Hit()
    {
        return _methodCacheDirect.TryGet<SamplePayload>(TestKey, out _);
    }

    [BenchmarkCategory("CacheHit"), Benchmark(Baseline = true)]
    public bool MemoryCache_Hit()
    {
        return _memoryCache.TryGet<SamplePayload>(TestKey, out _);
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

    [BenchmarkCategory("CacheHit"), Benchmark]
    public bool FastCache_Hit()
    {
        return _fastCache.TryGet<SamplePayload>(TestKey, out _);
    }

    // ==================== CACHE MISS + SET TESTS ====================

    [BenchmarkCategory("MissAndSet"), Benchmark]
    public async Task<SamplePayload> MethodCache_MissAndSet()
    {
        _methodCache.Remove($"{TestKey}_miss");
        return await _methodCache.GetOrSetAsync($"{TestKey}_miss", CreatePayloadAsync, TimeSpan.FromMinutes(10));
    }

    [BenchmarkCategory("MissAndSet"), Benchmark]
    public async Task<SamplePayload> MethodCacheStatic_MissAndSet()
    {
        _methodCacheStatic.Remove($"{TestKey}_miss");
        return await _methodCacheStatic.GetOrSetAsync($"{TestKey}_miss", CreatePayloadAsync, TimeSpan.FromMinutes(10));
    }

    [BenchmarkCategory("MissAndSet"), Benchmark]
    public async Task<SamplePayload> MethodCacheDirect_MissAndSet()
    {
        _methodCacheDirect.Remove($"{TestKey}_miss");
        return await _methodCacheDirect.GetOrSetAsync($"{TestKey}_miss", CreatePayloadAsync, TimeSpan.FromMinutes(10));
    }

    [BenchmarkCategory("MissAndSet"), Benchmark(Baseline = true)]
    public async Task<SamplePayload> MemoryCache_MissAndSet()
    {
        _memoryCache.Remove($"{TestKey}_miss");
        return await _memoryCache.GetOrSetAsync($"{TestKey}_miss", CreatePayloadAsync, TimeSpan.FromMinutes(10));
    }

    [BenchmarkCategory("MissAndSet"), Benchmark]
    public async Task<SamplePayload> FusionCache_MissAndSet()
    {
        _fusionCache.Remove($"{TestKey}_miss");
        return await _fusionCache.GetOrSetAsync($"{TestKey}_miss", CreatePayloadAsync, TimeSpan.FromMinutes(10));
    }

    [BenchmarkCategory("MissAndSet"), Benchmark]
    public async Task<SamplePayload> LazyCache_MissAndSet()
    {
        _lazyCache.Remove($"{TestKey}_miss");
        return await _lazyCache.GetOrSetAsync($"{TestKey}_miss", CreatePayloadAsync, TimeSpan.FromMinutes(10));
    }

    [BenchmarkCategory("MissAndSet"), Benchmark]
    public async Task<SamplePayload> EasyCaching_MissAndSet()
    {
        _easyCaching.Remove($"{TestKey}_miss");
        return await _easyCaching.GetOrSetAsync($"{TestKey}_miss", CreatePayloadAsync, TimeSpan.FromMinutes(10));
    }

    [BenchmarkCategory("MissAndSet"), Benchmark]
    public async Task<SamplePayload> FastCache_MissAndSet()
    {
        _fastCache.Remove($"{TestKey}_miss");
        return await _fastCache.GetOrSetAsync($"{TestKey}_miss", CreatePayloadAsync, TimeSpan.FromMinutes(10));
    }

    // ==================== CONCURRENT ACCESS TESTS ====================

    [Params(10, 100)]
    public int ConcurrentThreads { get; set; }

    [BenchmarkCategory("Concurrent"), Benchmark]
    public async Task MethodCache_Concurrent()
    {
        await RunConcurrentTest(_methodCache);
    }

    [BenchmarkCategory("Concurrent"), Benchmark]
    public async Task MethodCacheStatic_Concurrent()
    {
        await RunConcurrentTest(_methodCacheStatic);
    }

    [BenchmarkCategory("Concurrent"), Benchmark]
    public async Task MethodCacheDirect_Concurrent()
    {
        await RunConcurrentTest(_methodCacheDirect);
    }

    [BenchmarkCategory("Concurrent"), Benchmark(Baseline = true)]
    public async Task MemoryCache_Concurrent()
    {
        await RunConcurrentTest(_memoryCache);
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

    [BenchmarkCategory("Concurrent"), Benchmark]
    public async Task FastCache_Concurrent()
    {
        await RunConcurrentTest(_fastCache);
    }

    private async Task RunConcurrentTest(ICacheAdapter cache)
    {
        var tasks = new Task<SamplePayload>[ConcurrentThreads];
        var key = $"{TestKey}_concurrent_{Guid.NewGuid()}";

        for (int i = 0; i < ConcurrentThreads; i++)
        {
            tasks[i] = cache.GetOrSetAsync(key, CreatePayloadAsync, TimeSpan.FromMinutes(10));
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
    public async Task MethodCacheStatic_Stampede()
    {
        await RunStampedeTest(_methodCacheStatic);
    }

    [BenchmarkCategory("Stampede"), Benchmark]
    public async Task MethodCacheDirect_Stampede()
    {
        await RunStampedeTest(_methodCacheDirect);
    }

    [BenchmarkCategory("Stampede"), Benchmark(Baseline = true)]
    public async Task MemoryCache_Stampede()
    {
        await RunStampedeTest(_memoryCache);
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

    [BenchmarkCategory("Stampede"), Benchmark]
    public async Task FastCache_Stampede()
    {
        await RunStampedeTest(_fastCache);
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
        await Task.Delay(1); // Simulate minimal async work
        return new SamplePayload { Id = 1, Name = "Generated", Data = new byte[1024] };
    }

    private static async Task<SamplePayload> CreateSlowPayloadAsync()
    {
        await Task.Delay(50); // Simulate expensive operation
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
