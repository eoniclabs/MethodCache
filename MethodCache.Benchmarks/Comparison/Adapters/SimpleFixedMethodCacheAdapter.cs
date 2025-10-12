using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace MethodCache.Benchmarks.Comparison.Adapters;

/// <summary>
/// Simple fixed adapter - just avoids the worst performance issues
/// </summary>
public class SimpleFixedMethodCacheAdapter : ICacheAdapter
{
    private readonly ICacheManager _cacheManager;
    private readonly ICacheKeyGenerator _keyGenerator;
    private readonly IServiceProvider _serviceProvider;
    private readonly CacheStatistics _stats = new();

    // Cache these to avoid allocations
    private readonly CacheRuntimePolicy _cachedPolicy;
    private readonly object[] _emptyArgs = Array.Empty<object>();

    public string Name => "MethodCacheFixed";

    public SimpleFixedMethodCacheAdapter()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMethodCache(config =>
        {
            config.DefaultPolicy(builder => builder.WithDuration(TimeSpan.FromMinutes(10)));
        }, typeof(SimpleFixedMethodCacheAdapter).Assembly);

        _serviceProvider = services.BuildServiceProvider();
        _cacheManager = _serviceProvider.GetRequiredService<ICacheManager>();
        _keyGenerator = _serviceProvider.GetRequiredService<ICacheKeyGenerator>();

        // Pre-create policy to avoid allocation on every call
        _cachedPolicy = CacheRuntimePolicy.FromPolicy(
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
            _cachedPolicy,
            _keyGenerator
        );
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet<TValue>(string key, out TValue? value)
    {
        // KEY FIX: Use GetAwaiter().GetResult() instead of .AsTask().Wait()
        // This avoids the Task allocation and is more efficient for ValueTask
        var valueTask = _cacheManager.TryGetAsync<TValue>(key, _emptyArgs, _cachedPolicy, _keyGenerator);

        // For synchronous completion (should be always for in-memory cache)
        if (valueTask.IsCompletedSuccessfully)
        {
            value = valueTask.Result;
        }
        else
        {
            // Fallback for async completion - use GetAwaiter instead of AsTask
            value = valueTask.AsTask().GetAwaiter().GetResult();
        }

        var found = value != null && !EqualityComparer<TValue>.Default.Equals(value, default);
        if (found)
            _stats.Hits++;
        else
            _stats.Misses++;
        return found;
    }

    public void Set<TValue>(string key, TValue value, TimeSpan duration)
    {
        // Use the cached policy to avoid allocation
        var task = _cacheManager.GetOrCreateAsync<TValue>(
            key,
            _emptyArgs,
            () => Task.FromResult(value),
            _cachedPolicy,
            _keyGenerator
        );

        // Use GetAwaiter().GetResult() instead of Wait()
        task.GetAwaiter().GetResult();
    }

    public void Remove(string key)
    {
        _cacheManager.InvalidateByKeysAsync(key).GetAwaiter().GetResult();
    }

    public void Clear()
    {
        // No-op
    }

    public CacheStatistics GetStatistics() => _stats;

    public void Dispose()
    {
        (_serviceProvider as IDisposable)?.Dispose();
    }
}