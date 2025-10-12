using System.Diagnostics;
using ZiggyCreatures.Caching.Fusion;

namespace MethodCache.Benchmarks.Comparison.Adapters;

/// <summary>
/// Adapter for FusionCache
/// </summary>
public class FusionCacheAdapter : ICacheAdapter
{
    private readonly FusionCache _cache;
    private readonly CacheStatistics _stats = new();

    public string Name => "FusionCache";

    public FusionCacheAdapter()
    {
        _cache = new FusionCache(new FusionCacheOptions());
    }

    public async Task<TValue> GetOrSetAsync<TValue>(
        string key,
        Func<Task<TValue>> factory,
        TimeSpan duration)
    {
        return await _cache.GetOrSetAsync<TValue>(
            key,
            async (ctx, ct) =>
            {
                _stats.Misses++;
                var sw = Stopwatch.StartNew();
                var result = await factory();
                sw.Stop();
                _stats.FactoryCalls++;
                _stats.TotalFactoryDuration += sw.Elapsed;

                return result;
            },
            options => options.SetDuration(duration)
        );
    }

    public bool TryGet<TValue>(string key, out TValue? value)
    {
        var result = _cache.TryGet<TValue>(key);
        value = result.Value;
        if (result.HasValue)
            _stats.Hits++;
        else
            _stats.Misses++;
        return result.HasValue;
    }

    public void Set<TValue>(string key, TValue value, TimeSpan duration)
    {
        _cache.Set(key, value, duration);
    }

    public void Remove(string key)
    {
        _cache.Remove(key);
    }

    public void Clear()
    {
        _cache.Clear();
    }

    public CacheStatistics GetStatistics() => _stats;

    public void Dispose()
    {
        _cache.Dispose();
    }
}
