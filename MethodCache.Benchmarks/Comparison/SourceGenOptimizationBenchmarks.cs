using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using MethodCache.Benchmarks.Comparison.Services;
using MethodCache.Core.Infrastructure.Extensions;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.KeyGeneration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace MethodCache.Benchmarks.Comparison;

[SimpleJob(RuntimeMoniker.Net90, invocationCount: 1000, iterationCount: 5)]
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 0, exportDiff: true)]
public class SourceGenOptimizationBenchmarks
{
    private IServiceProvider _serviceProvider = null!;
    private IMethodCacheBenchmarkService _decoratedService = null!;
    private IMemoryCache _memoryCache = null!;
    private const string TestKey = "test-key-123";
    private readonly SamplePayload _testPayload = new() { Id = 123, Name = "cached-value", Data = new byte[1024] };

    [GlobalSetup]
    public void GlobalSetup()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Configure MethodCache with the generated decorator
        services.AddMethodCache(config =>
        {
            config.DefaultPolicy(builder => builder.WithDuration(TimeSpan.FromMinutes(10)));
        }, typeof(SourceGenOptimizationBenchmarks).Assembly);

        // Register the benchmark service with caching using the generated extension method
        services.AddIMethodCacheBenchmarkServiceWithCachingSingleton(sp =>
            new MethodCacheBenchmarkService());

        // Also setup raw MemoryCache for comparison
        services.AddMemoryCache(options =>
        {
            options.SizeLimit = null;
            options.TrackStatistics = false;
        });

        _serviceProvider = services.BuildServiceProvider();
        _decoratedService = _serviceProvider.GetRequiredService<IMethodCacheBenchmarkService>();
        _memoryCache = _serviceProvider.GetRequiredService<IMemoryCache>();

        // Warm up both caches
        _ = _decoratedService.GetAsync(TestKey).Result;
        _memoryCache.Set(TestKey, _testPayload, TimeSpan.FromMinutes(10));

        // Verify cache is warm
        var warmupResult = _decoratedService.GetAsync(TestKey).Result;
        if (warmupResult?.Id != _testPayload.Id)
        {
            throw new InvalidOperationException("Cache warmup failed for decorated service");
        }

        var memoryCacheResult = _memoryCache.Get<SamplePayload>(TestKey);
        if (memoryCacheResult?.Id != _testPayload.Id)
        {
            throw new InvalidOperationException("Cache warmup failed for MemoryCache");
        }
    }

    [Benchmark(Description = "SourceGen with Ultra-fast Path")]
    public async Task<SamplePayload> SourceGenOptimized_CacheHit()
    {
        return await _decoratedService.GetAsync(TestKey);
    }

    [Benchmark(Description = "SourceGen Sync Method")]
    public SamplePayload SourceGenOptimized_SyncCacheHit()
    {
        return _decoratedService.Get(TestKey);
    }

    [Benchmark(Baseline = true, Description = "Raw MemoryCache")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SamplePayload MemoryCache_CacheHit()
    {
        return _memoryCache.Get<SamplePayload>(TestKey)!;
    }

    [Benchmark(Description = "Raw MemoryCache with TryGetValue")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SamplePayload MemoryCache_TryGetValue()
    {
        _memoryCache.TryGetValue(TestKey, out SamplePayload? value);
        return value!;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        (_serviceProvider as IDisposable)?.Dispose();
    }
}