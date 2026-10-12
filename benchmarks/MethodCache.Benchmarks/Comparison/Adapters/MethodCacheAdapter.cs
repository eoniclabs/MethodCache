using System.Diagnostics;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Benchmarks.Comparison.Adapters;

/// <summary>
/// Adapter for MethodCache (using direct ICacheManager for fair comparison)
/// </summary>
public class MethodCacheAdapter : ICacheAdapter
{
    private readonly ICacheManager _cacheManager;
    private readonly ICacheKeyGenerator _keyGenerator;
    private readonly IMemoryCache _memoryCache;
    private readonly IServiceProvider _serviceProvider;
    private readonly CacheStatistics _stats = new();

    public string Name => "MethodCache (Legacy Runtime)";

    public MethodCacheAdapter(TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (timeProvider != null) services.AddSingleton(timeProvider);
        services.AddMethodCache(config =>
        {
            config.DefaultPolicy(builder => builder.WithDuration(TimeSpan.FromMinutes(10)));
        }); // Don't scan assembly to avoid conflicts

        _serviceProvider = services.BuildServiceProvider();
        _cacheManager = _serviceProvider.GetRequiredService<ICacheManager>();
        _keyGenerator = _serviceProvider.GetRequiredService<ICacheKeyGenerator>();
        _memoryCache = (IMemoryCache)_cacheManager;
    }

    public async Task<TValue> GetOrSetAsync<TValue>(
        string key,
        Func<Task<TValue>> factory,
        TimeSpan duration)
    {
        var descriptor = CacheRuntimePolicy.FromPolicy(
            key,
            CachePolicy.Empty with { Duration = duration },
            CachePolicyFields.Duration
        );

        return await _cacheManager.GetOrCreateAsync<TValue>(
            key,
            Array.Empty<object>(),
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
            descriptor,
            _keyGenerator
        );
    }

    public bool TryGet<TValue>(string key, out TValue? value)
    {
        var descriptor = CacheRuntimePolicy.FromPolicy(
            key,
            CachePolicy.Empty,
            CachePolicyFields.None
        );

        var cacheKey = _keyGenerator.GenerateKey(key, Array.Empty<object>(), descriptor);
        var found = _cacheManager.TryGetFast(cacheKey, out value);
        if (found)
            _stats.Hits++;
        else
            _stats.Misses++;
        return found;
    }

    public void Set<TValue>(string key, TValue value, TimeSpan duration)
    {
        var policy = CacheRuntimePolicy.Empty(key);
        var cacheKey = _keyGenerator.GenerateKey(key, Array.Empty<object>(), policy);
        _memoryCache.SetAsync(cacheKey, value, duration).GetAwaiter().GetResult();
    }

    public void Remove(string key)
    {
        var cacheKey = _keyGenerator.GenerateKey(key, Array.Empty<object>(), CacheRuntimePolicy.Empty(key));
        _memoryCache.RemoveAsync(cacheKey).GetAwaiter().GetResult();
    }

    public void Clear() => _memoryCache.ClearAsync().GetAwaiter().GetResult();

    public CacheStatistics GetStatistics() => _stats;

    public void Dispose()
    {
        (_serviceProvider as IDisposable)?.Dispose();
    }
}
