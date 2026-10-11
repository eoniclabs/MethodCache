using LazyCache;
using System.Diagnostics;

namespace MethodCache.Benchmarks.Comparison.Adapters;

/// <summary>
/// Adapter for LazyCache
/// </summary>
public class LazyCacheAdapter : ICacheAdapter
{
    private readonly IAppCache _cache;
    private readonly CacheStatistics _stats = new();

    public string Name => "LazyCache";

    public LazyCacheAdapter()
    {
        _cache = new CachingService();
    }

    public async Task<TValue> GetOrSetAsync<TValue>(
        string key,
        Func<Task<TValue>> factory,
        TimeSpan duration)
    {
        return await _cache.GetOrAddAsync(key, async () =>
        {
            _stats.Misses++;
            var sw = Stopwatch.StartNew();
            var result = await factory();
            sw.Stop();
            _stats.FactoryCalls++;
            _stats.TotalFactoryDuration += sw.Elapsed;

            return result;
        }, duration);
    }

    public bool TryGet<TValue>(string key, out TValue? value)
    {
        var found = _cache.TryGetValue(key, out value);
        if (found)
            _stats.Hits++;
        else
            _stats.Misses++;
        return found;
    }

    public void Set<TValue>(string key, TValue value, TimeSpan duration)
    {
        _cache.Add(key, value, duration);
    }

    public void Remove(string key)
    {
        _cache.Remove(key);
    }

    public void Clear()
    {
        // LazyCache doesn't expose a clear method
        // Would need to track keys or recreate cache
    }

    public CacheStatistics GetStatistics() => _stats;

    public void Dispose()
    {
        // LazyCache doesn't implement IDisposable
    }
}
