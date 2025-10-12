using Microsoft.Extensions.Caching.Memory;
using System.Diagnostics;

namespace MethodCache.Benchmarks.Comparison.Adapters;

/// <summary>
/// Adapter for Microsoft.Extensions.Caching.Memory (baseline)
/// </summary>
public class MemoryCacheAdapter : ICacheAdapter
{
    private readonly IMemoryCache _cache;
    private readonly CacheStatistics _stats = new();

    public string Name => "Microsoft.Extensions.Caching.Memory";

    public MemoryCacheAdapter()
    {
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = null, // No size limit for fair comparison
            CompactionPercentage = 0.05,
            ExpirationScanFrequency = TimeSpan.FromMinutes(5)
        });
    }

    public async Task<TValue> GetOrSetAsync<TValue>(
        string key,
        Func<Task<TValue>> factory,
        TimeSpan duration)
    {
        return await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.SetAbsoluteExpiration(duration);

            _stats.Misses++;
            var sw = Stopwatch.StartNew();
            var result = await factory();
            sw.Stop();
            _stats.FactoryCalls++;
            _stats.TotalFactoryDuration += sw.Elapsed;

            return result;
        }) ?? throw new InvalidOperationException("Factory returned null");
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
        _cache.Set(key, value, duration);
    }

    public void Remove(string key)
    {
        _cache.Remove(key);
    }

    public void Clear()
    {
        // MemoryCache doesn't have a clear method
        // We'd need to track keys or recreate the cache
        // For now, just leave it as no-op
    }

    public CacheStatistics GetStatistics() => _stats;

    public void Dispose()
    {
        _cache.Dispose();
    }
}
