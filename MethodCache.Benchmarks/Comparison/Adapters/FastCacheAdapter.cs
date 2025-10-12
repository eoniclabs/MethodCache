using MethodCache.Benchmarks.Comparison.FastCache;
using System.Diagnostics;

namespace MethodCache.Benchmarks.Comparison.Adapters;

/// <summary>
/// FastCache adapter - Simple ConcurrentDictionary-based cache
/// Uses ConcurrentDictionary with efficient TTL tracking via Environment.TickCount64
/// This implementation follows the FastCache concept: minimal overhead, maximum speed
/// </summary>
public class FastCacheAdapter : ICacheAdapter
{
    private readonly SimpleFastCache<string, SamplePayload> _cache;
    private readonly CacheStatistics _stats = new();

    public string Name => "FastCache";

    public FastCacheAdapter()
    {
        _cache = new SimpleFastCache<string, SamplePayload>();
    }

    public async Task<TValue> GetOrSetAsync<TValue>(
        string key,
        Func<Task<TValue>> factory,
        TimeSpan duration)
    {
        if (typeof(TValue) != typeof(SamplePayload))
        {
            throw new InvalidOperationException("FastCache adapter only supports SamplePayload");
        }

        // FastCache uses Store/Fetch pattern
        var cached = _cache.Fetch(key);
        if (cached != null)
        {
            _stats.Hits++;
            return (TValue)(object)cached;
        }

        // Cache miss - execute factory
        _stats.Misses++;
        _stats.FactoryCalls++;
        var sw = Stopwatch.StartNew();
        var result = await factory();
        sw.Stop();
        _stats.TotalFactoryDuration += sw.Elapsed;

        // Store with TTL
        _cache.Store(key, (SamplePayload)(object)result!, duration);

        return result;
    }

    public bool TryGet<TValue>(string key, out TValue? value)
    {
        if (typeof(TValue) != typeof(SamplePayload))
        {
            throw new InvalidOperationException("FastCache adapter only supports SamplePayload");
        }

        var cached = _cache.Fetch(key);

        if (cached != null)
        {
            _stats.Hits++;
            value = (TValue)(object)cached;
            return true;
        }

        _stats.Misses++;
        value = default;
        return false;
    }

    public void Set<TValue>(string key, TValue value, TimeSpan duration)
    {
        if (typeof(TValue) != typeof(SamplePayload))
        {
            throw new InvalidOperationException("FastCache adapter only supports SamplePayload");
        }

        _cache.Store(key, (SamplePayload)(object)value!, duration);
    }

    public void Remove(string key)
    {
        _cache.Invalidate(key);
    }

    public void Clear()
    {
        _cache.Clear();
    }

    public CacheStatistics GetStatistics() => _stats;

    public void Dispose()
    {
        // FastCache doesn't implement IDisposable
        // Cleanup handled by GC
    }
}
