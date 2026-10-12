using LazyCache;
using LazyCache.Providers;
using Microsoft.Extensions.Caching.Memory;
using System.Diagnostics;

namespace MethodCache.Benchmarks.Comparison.Adapters;

/// <summary>
/// Adapter for LazyCache
/// </summary>
public class LazyCacheAdapter : ICacheAdapter
{
    private readonly IAppCache _cache;
    private readonly MemoryCache _memoryCache;
    private readonly CacheStatistics _stats = new();

    public string Name => "LazyCache";

    public LazyCacheAdapter()
    {
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        _cache = new CachingService(new MemoryCacheProvider(_memoryCache));
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
        }, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = duration });
    }

    public bool TryGet<TValue>(string key, out TValue? value)
    {
        var found = _memoryCache.TryGetValue(key, out _);
        value = found ? _cache.Get<TValue>(key) : default;
        if (found)
            _stats.Hits++;
        else
            _stats.Misses++;
        return found;
    }

    public void Set<TValue>(string key, TValue value, TimeSpan duration)
    {
        _cache.Add(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = duration });
    }

    public void Remove(string key)
    {
        _cache.Remove(key);
    }

    public void Clear()
    {
        _memoryCache.Clear();
    }

    public CacheStatistics GetStatistics() => _stats;

    public void Dispose()
    {
        _memoryCache.Dispose();
    }
}
