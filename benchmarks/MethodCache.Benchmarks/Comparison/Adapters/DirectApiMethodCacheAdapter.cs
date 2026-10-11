using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Storage.Abstractions;

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
    private const string ManualMethodName = "ManualKey";

    private readonly IMemoryCache _cache;
    private readonly ICacheManager _cacheManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly CacheStatistics _stats = new();

    public string Name => "MethodCache (Manual Key)";

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

        // Access both the high-level cache manager (for fast path helpers)
        // and the raw IMemoryCache interface for maintenance operations.
        _cacheManager = _serviceProvider.GetRequiredService<ICacheManager>();
        _cache = (IMemoryCache)_cacheManager;
    }

    public async Task<TValue> GetOrSetAsync<TValue>(
        string key,
        Func<Task<TValue>> factory,
        TimeSpan duration)
    {
        // Try the optimized fast-path lookup first (no key generation, no extra allocations)
        var cachedTask = _cacheManager.TryGetFastAsync<TValue>(key);
        var cached = cachedTask.IsCompletedSuccessfully
            ? cachedTask.Result
            : await cachedTask.ConfigureAwait(false);

        if (cached != null && !EqualityComparer<TValue>.Default.Equals(cached, default))
        {
            _stats.Hits++;
            return cached;
        }

        // Cache miss - execute factory
        _stats.Misses++;

        // Build a minimal runtime policy (duration only) so the manual path can
        // take advantage of lightweight coordination within InMemoryCacheManager.
        var policy = CacheRuntimePolicy.FromPolicy(
            key,
            CachePolicy.Empty with { Duration = duration },
            CachePolicyFields.Duration);

        // Use the fast-path setter; this will coordinate concurrent misses without TCS churn
        // and only execute the factory for the winning request.
        return await _cacheManager.GetOrCreateFastAsync(
            key,
            ManualMethodName,
            async () =>
            {
                _stats.FactoryCalls++;
                var sw = Stopwatch.StartNew();
                var created = await factory().ConfigureAwait(false);
                sw.Stop();
                _stats.TotalFactoryDuration += sw.Elapsed;
                return created;
            },
            policy).ConfigureAwait(false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet<TValue>(string key, out TValue? value)
    {
        var valueTask = _cacheManager.TryGetFastAsync<TValue>(key);
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
