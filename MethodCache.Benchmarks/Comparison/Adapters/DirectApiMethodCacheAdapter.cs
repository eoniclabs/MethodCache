using MethodCache.Core.Storage.Abstractions;
using MethodCache.Core.Runtime;
using MethodCache.Core.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace MethodCache.Benchmarks.Comparison.Adapters;

/// <summary>
/// MethodCache adapter using direct IMemoryCache API with user-provided keys.
/// This is the FAIREST comparison - uses MethodCache exactly like other frameworks use their APIs.
///
/// No source generation, no key generation, no overhead.
/// Just direct cache access with manual keys, like:
///   cache.GetAsync("my-key")
///   cache.SetAsync("my-key", value, duration)
///
/// This demonstrates that MethodCache's underlying cache storage is competitive
/// with other frameworks when used the same way (manual keys, no code generation).
/// </summary>
public class DirectApiMethodCacheAdapter : ICacheAdapter
{
    private readonly IMemoryCache _cache;
    private readonly IServiceProvider _serviceProvider;
    private readonly CacheStatistics _stats = new();

    public string Name => "DirectApiMethodCache";

    public DirectApiMethodCacheAdapter()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Add MethodCache infrastructure (just the storage, no source generation)
        services.AddMethodCache(config =>
        {
            config.DefaultPolicy(builder => builder.WithDuration(TimeSpan.FromMinutes(10)));
        }); // Don't scan assembly to avoid conflicts

        _serviceProvider = services.BuildServiceProvider();

        // Get the underlying IMemoryCache directly
        // This is MethodCache's cache storage without any wrappers
        var cacheManager = _serviceProvider.GetRequiredService<ICacheManager>();
        _cache = (IMemoryCache)cacheManager;
    }

    public async Task<TValue> GetOrSetAsync<TValue>(
        string key,
        Func<Task<TValue>> factory,
        TimeSpan duration)
    {
        // Direct cache access with user key - NO KEY GENERATION!
        var valueTask = _cache.GetAsync<TValue>(key);
        var cached = valueTask.IsCompletedSuccessfully
            ? valueTask.Result
            : await valueTask;

        if (cached != null && !EqualityComparer<TValue>.Default.Equals(cached, default))
        {
            _stats.Hits++;
            return cached;
        }

        // Cache miss - execute factory
        _stats.Misses++;
        _stats.FactoryCalls++;
        var sw = Stopwatch.StartNew();
        var result = await factory();
        sw.Stop();
        _stats.TotalFactoryDuration += sw.Elapsed;

        // Set in cache with user key
        await _cache.SetAsync(key, result, duration);

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet<TValue>(string key, out TValue? value)
    {
        // Direct cache Get with user-provided key
        // This is IDENTICAL to how LazyCache, FusionCache, etc. work
        var valueTask = _cache.GetAsync<TValue>(key);

        value = valueTask.IsCompletedSuccessfully
            ? valueTask.Result
            : valueTask.GetAwaiter().GetResult();

        var found = value != null && !EqualityComparer<TValue>.Default.Equals(value, default);

        if (found)
            _stats.Hits++;
        else
            _stats.Misses++;

        return found;
    }

    public void Set<TValue>(string key, TValue value, TimeSpan duration)
    {
        // Direct cache Set with user-provided key
        var valueTask = _cache.SetAsync(key, value, duration);

        if (!valueTask.IsCompleted)
        {
            valueTask.GetAwaiter().GetResult();
        }
    }

    public void Remove(string key)
    {
        var valueTask = _cache.RemoveAsync(key);
        if (!valueTask.IsCompleted)
        {
            valueTask.GetAwaiter().GetResult();
        }
    }

    public void Clear()
    {
        var valueTask = _cache.ClearAsync();
        if (!valueTask.IsCompleted)
        {
            valueTask.GetAwaiter().GetResult();
        }
    }

    public CacheStatistics GetStatistics() => _stats;

    public void Dispose()
    {
        _cache?.Dispose();
        (_serviceProvider as IDisposable)?.Dispose();
    }
}
