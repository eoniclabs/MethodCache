using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Diagnosers;
using MethodCache.Benchmarks.Comparison.Services;
using MethodCache.Benchmarks.Comparison;
using MethodCache.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core.Infrastructure.Extensions;
using MethodCache.Providers.Memory.Extensions;

namespace MethodCache.Benchmarks.Microbenchmarks;

/// <summary>
/// Profiles the SourceGen sync hit path to identify the 1μs bottleneck.
/// Breaks down each layer to isolate where time is spent.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90, warmupCount: 3, iterationCount: 10)]
[RankColumn]
public class SourceGenSyncPathProfiler
{
    private IMethodCacheBenchmarkService _sourceGenService = null!;
    private ICacheManager _cacheManager = null!;
    private const string TestKey = "benchmark-key-123";
    private const string CacheKey = "GetAsync:benchmark-key-123";

    [GlobalSetup]
    public void Setup()
    {
        // Set up the full SourceGen pipeline exactly like UnifiedCacheComparisonBenchmarks
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMethodCache(config =>
        {
            config.DefaultPolicy(builder => builder.WithDuration(TimeSpan.FromMinutes(10)));
        });

        services.Configure<MethodCache.Core.Configuration.MemoryCacheOptions>(opts =>
        {
            opts.EnableStatistics = false;
            opts.EvictionPolicy = MethodCache.Core.Configuration.MemoryCacheEvictionPolicy.LRU;
        });

        services.AddAdvancedMemoryStorage(opts =>
        {
            opts.EvictionPolicy = MethodCache.Providers.Memory.Configuration.EvictionPolicy.LRU;
        });

        services.AddSingleton<MethodCacheBenchmarkService>();
        services.AddIMethodCacheBenchmarkServiceWithCaching(sp =>
            sp.GetRequiredService<MethodCacheBenchmarkService>());

        var serviceProvider = services.BuildServiceProvider();
        _cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
        _sourceGenService = serviceProvider.GetRequiredService<IMethodCacheBenchmarkService>();

        // Pre-populate cache with test data
        var payload = new SamplePayload
        {
            Id = 1,
            Name = "Test",
            Data = new byte[100]
        };

        // Warm up the cache
        _sourceGenService.Set(TestKey, payload);

        // Verify it's cached
        var result = _sourceGenService.GetAsync(TestKey).GetAwaiter().GetResult();
        if (result == null)
        {
            throw new InvalidOperationException("Cache warmup failed");
        }
    }

    // ========================================================================
    // Layer 1: Raw cache manager call (should be ~200ns)
    // ========================================================================

    [Benchmark(Baseline = true, Description = "Direct TryGetFastAsync (raw cache)")]
    public async Task<SamplePayload?> Layer1_DirectCacheManager()
    {
        var task = _cacheManager.TryGetFastAsync<SamplePayload>(CacheKey);
        if (task.IsCompletedSuccessfully)
        {
            return task.Result;
        }
        return await task.ConfigureAwait(false);
    }

    // ========================================================================
    // Layer 2: Through generated decorator async (should be ~300-400ns)
    // ========================================================================

    [Benchmark(Description = "SourceGen GetAsync (async path)")]
    public async Task<SamplePayload> Layer2_SourceGenAsync()
    {
        return await _sourceGenService.GetAsync(TestKey);
    }

    // ========================================================================
    // Layer 3: Sync wrapper with IsCompletedSuccessfully check (the 1μs path)
    // ========================================================================

    [Benchmark(Description = "SourceGen GetAsync + sync wrapper (slow path)")]
    public SamplePayload Layer3_SourceGenSyncWrapper()
    {
        var task = _sourceGenService.GetAsync(TestKey);

        // This is what MethodCacheSourceGenAdapter.TryGet does
        if (task.IsCompletedSuccessfully)
        {
            return task.Result;
        }
        else
        {
            // Sync-over-async - should rarely hit for cache hits
            return task.GetAwaiter().GetResult();
        }
    }

    // ========================================================================
    // Layer 4: Alternative - use Task.Run to avoid sync-over-async
    // ========================================================================

    [Benchmark(Description = "SourceGen with Task.Run wrapper")]
    public SamplePayload Layer4_TaskRunWrapper()
    {
        return Task.Run(async () => await _sourceGenService.GetAsync(TestKey)).GetAwaiter().GetResult();
    }

    // ========================================================================
    // Diagnostic: Check if the task is actually completing synchronously
    // ========================================================================

    [Benchmark(Description = "Check IsCompletedSuccessfully hit rate")]
    public bool Diagnostic_IsCompletedSuccessfully()
    {
        var task = _sourceGenService.GetAsync(TestKey);
        return task.IsCompletedSuccessfully;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        (_cacheManager as IDisposable)?.Dispose();
    }
}
