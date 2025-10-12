using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.Core.Runtime.Execution;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace MethodCache.Benchmarks.Comparison.Adapters;

/// <summary>
/// Properly optimized adapter that uses direct IMemoryCache access and proper ValueTask handling
/// </summary>
public class ProperlyOptimizedMethodCacheAdapter : ICacheAdapter
{
    private readonly IMemoryCache _memoryCache;
    private readonly ICacheKeyGenerator _keyGenerator;
    private readonly IServiceProvider _serviceProvider;
    private readonly CacheStatistics _stats = new();

    // Cached instances to avoid allocations
    private readonly CacheRuntimePolicy _defaultPolicy;
    private readonly object[] _emptyArgs = Array.Empty<object>();

    public string Name => "ProperlyOptimized";

    public ProperlyOptimizedMethodCacheAdapter()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMethodCache(config =>
        {
            config.DefaultPolicy(builder => builder.WithDuration(TimeSpan.FromMinutes(10)));
        }, typeof(ProperlyOptimizedMethodCacheAdapter).Assembly);

        _serviceProvider = services.BuildServiceProvider();

        // Get ICacheManager and cast to IMemoryCache
        // InMemoryCacheManager implements both ICacheManager and IMemoryCache
        var cacheManager = _serviceProvider.GetRequiredService<ICacheManager>();
        _memoryCache = (IMemoryCache)cacheManager;
        _keyGenerator = _serviceProvider.GetRequiredService<ICacheKeyGenerator>();

        // Pre-create a default policy to avoid allocations
        _defaultPolicy = CacheRuntimePolicy.FromPolicy(
            "default",
            CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(10) },
            CachePolicyFields.Duration
        );
    }

    public async Task<TValue> GetOrSetAsync<TValue>(
        string key,
        Func<Task<TValue>> factory,
        TimeSpan duration)
    {
        // Generate key once
        var cacheKey = _keyGenerator.GenerateKey(key, _emptyArgs, _defaultPolicy);

        // Try get first
        var valueTask = _memoryCache.GetAsync<TValue>(cacheKey);
        var cached = valueTask.IsCompletedSuccessfully
            ? valueTask.Result
            : await valueTask;

        if (cached != null && !EqualityComparer<TValue>.Default.Equals(cached, default))
        {
            return cached;
        }

        // Cache miss - execute factory
        _stats.Misses++;
        var sw = Stopwatch.StartNew();
        var result = await factory();
        sw.Stop();
        _stats.FactoryCalls++;
        _stats.TotalFactoryDuration += sw.Elapsed;

        // Set in cache
        await _memoryCache.SetAsync(cacheKey, result, duration);

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet<TValue>(string key, out TValue? value)
    {
        // Generate the cache key once
        var cacheKey = _keyGenerator.GenerateKey(key, _emptyArgs, _defaultPolicy);

        // Use IMemoryCache.GetAsync directly (bypasses method name overhead)
        var valueTask = _memoryCache.GetAsync<TValue>(cacheKey);

        // OPTIMIZATION: Use GetAwaiter().GetResult() directly on ValueTask
        // This avoids the Task allocation that AsTask() causes
        value = valueTask.IsCompletedSuccessfully
            ? valueTask.Result
            : valueTask.GetAwaiter().GetResult();  // No AsTask() call!

        var found = value != null && !EqualityComparer<TValue>.Default.Equals(value, default);

        if (found)
            _stats.Hits++;
        else
            _stats.Misses++;

        return found;
    }

    public void Set<TValue>(string key, TValue value, TimeSpan duration)
    {
        // Generate the cache key
        var cacheKey = _keyGenerator.GenerateKey(key, _emptyArgs, _defaultPolicy);

        // IMPORTANT: Block synchronously to ensure warmup completes
        // Use GetAwaiter().GetResult() to properly handle ValueTask
        var valueTask = _memoryCache.SetAsync(cacheKey, value, duration);

        if (!valueTask.IsCompleted)
        {
            valueTask.GetAwaiter().GetResult();
        }
    }

    public void Remove(string key)
    {
        var cacheKey = _keyGenerator.GenerateKey(key, _emptyArgs, _defaultPolicy);
        var valueTask = _memoryCache.RemoveAsync(cacheKey);
        if (!valueTask.IsCompleted)
        {
            valueTask.GetAwaiter().GetResult();
        }
    }

    public void Clear()
    {
        var valueTask = _memoryCache.ClearAsync();
        if (!valueTask.IsCompleted)
        {
            valueTask.GetAwaiter().GetResult();
        }
    }

    public CacheStatistics GetStatistics() => _stats;

    public void Dispose()
    {
        (_serviceProvider as IDisposable)?.Dispose();
    }
}
