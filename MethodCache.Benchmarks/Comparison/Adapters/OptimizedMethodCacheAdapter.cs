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

    public OptimizedMethodCacheAdapter()
    {
        var services = new ServiceCollection();
        services.AddLogging();
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
            _defaultPolicy,
            _keyGenerator
        );
    }

    public bool TryGet<TValue>(string key, out TValue? value)
    {
        // OPTIMIZATION: Direct synchronous path to memory cache
        // Generate the actual cache key once
        var cacheKey = _keyGenerator.GenerateKey(key, _emptyArgs, _defaultPolicy);

        // Use GetAsync which returns ValueTask (not async/await to avoid state machine)
        var valueTask = _memoryCache.GetAsync<TValue>(cacheKey);

        // ValueTask.IsCompletedSuccessfully is true for synchronous completion
        if (valueTask.IsCompletedSuccessfully)
        {
            value = valueTask.Result;
            var found = value != null && !EqualityComparer<TValue>.Default.Equals(value, default);

            if (found)
                _stats.Hits++;
            else
                _stats.Misses++;

            return found;
        }

        // Fallback: If not completed synchronously (shouldn't happen for in-memory)
        // We have to block, but this should be rare
        value = valueTask.AsTask().GetAwaiter().GetResult();
        var foundAsync = value != null && !EqualityComparer<TValue>.Default.Equals(value, default);

        if (foundAsync)
            _stats.Hits++;
        else
            _stats.Misses++;

        return foundAsync;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set<TValue>(string key, TValue value, TimeSpan duration)
    {
        // Use fire-and-forget for Set to avoid blocking
        _ = SetAsyncInternal(key, value, duration);
    }

    private async Task SetAsyncInternal<TValue>(string key, TValue value, TimeSpan duration)
    {
        // Generate the cache key
        var cacheKey = _keyGenerator.GenerateKey(key, _emptyArgs, _defaultPolicy);

        // Set directly in memory cache
        await _memoryCache.SetAsync(cacheKey, value, duration);
    }

    public void Remove(string key)
    {
        // Fire and forget for remove
        _ = _cacheManager.InvalidateByKeysAsync(key);
    }

    public void Clear()
    {
        // Fire and forget for clear
        if (_memoryCache != null)
        {
            _ = _memoryCache.ClearAsync();
        }
    }

    public CacheStatistics GetStatistics() => _stats;

    public void Dispose()
    {
        (_serviceProvider as IDisposable)?.Dispose();
    }
}