using System.Diagnostics;
using System.Runtime.CompilerServices;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Benchmarks.Comparison.Adapters;

/// <summary>
/// Optimized adapter for MethodCache that minimizes allocations and avoids blocking
/// </summary>
public class OptimizedMethodCacheAdapter : ICacheAdapter
{
    private readonly ICacheManager _cacheManager;
    private readonly IMemoryCache _memoryCache; // Direct access to memory cache for sync operations
    private readonly ICacheKeyGenerator _keyGenerator;
    private readonly IServiceProvider _serviceProvider;
    private readonly CacheStatistics _stats = new();

    // Cached instances to avoid allocations
    private readonly CacheRuntimePolicy _defaultPolicy;
    private readonly object[] _emptyArgs = Array.Empty<object>();

    public string Name => "OptimizedMethodCache";

    public OptimizedMethodCacheAdapter(TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (timeProvider != null) services.AddSingleton(timeProvider);
        services.AddMethodCache(config =>
        {
            config.DefaultPolicy(builder => builder.WithDuration(TimeSpan.FromMinutes(10)));
        }, typeof(OptimizedMethodCacheAdapter).Assembly);

        _serviceProvider = services.BuildServiceProvider();
        _cacheManager = _serviceProvider.GetRequiredService<ICacheManager>();
        _keyGenerator = _serviceProvider.GetRequiredService<ICacheKeyGenerator>();

        // Try to get direct access to the memory cache for synchronous operations
        if (_cacheManager is InMemoryCacheManager inMemoryManager)
        {
            _memoryCache = inMemoryManager;
        }
        else
        {
            // Fallback if not using InMemoryCacheManager
            _memoryCache = _serviceProvider.GetRequiredService<IMemoryCache>();
        }

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
        // For GetOrSet, we still need to use the async path
        return await _cacheManager.GetOrCreateAsync<TValue>(
            key,
            _emptyArgs,
            async () =>
            {
                _stats.Misses++;
                var sw = Stopwatch.StartNew();
                var result = await factory();
                sw.Stop();
                _stats.FactoryCalls++;
                _stats.TotalFactoryDuration += sw.Elapsed;

                return result;
            },
            _defaultPolicy with { Duration = duration },
            _keyGenerator
        );
    }

    public bool TryGet<TValue>(string key, out TValue? value)
    {
        // OPTIMIZATION: Direct synchronous path to memory cache
        // Generate the actual cache key once
        var cacheKey = _keyGenerator.GenerateKey(key, _emptyArgs, _defaultPolicy);

        var found = _cacheManager.TryGetFast(cacheKey, out value);
        if (found) _stats.Hits++;
        else _stats.Misses++;
        return found;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set<TValue>(string key, TValue value, TimeSpan duration)
    {
        var cacheKey = _keyGenerator.GenerateKey(key, _emptyArgs, _defaultPolicy);
        _memoryCache.SetAsync(cacheKey, value, duration).GetAwaiter().GetResult();
    }

    public void Remove(string key)
    {
        var cacheKey = _keyGenerator.GenerateKey(key, _emptyArgs, _defaultPolicy);
        _memoryCache.RemoveAsync(cacheKey).GetAwaiter().GetResult();
    }

    public void Clear() => _memoryCache.ClearAsync().GetAwaiter().GetResult();

    public CacheStatistics GetStatistics() => _stats;

    public void Dispose()
    {
        (_serviceProvider as IDisposable)?.Dispose();
    }
}