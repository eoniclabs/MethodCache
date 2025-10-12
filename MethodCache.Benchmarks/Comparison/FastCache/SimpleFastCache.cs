using System.Collections.Concurrent;

namespace MethodCache.Benchmarks.Comparison.FastCache;

/// <summary>
/// Simple high-performance cache based on FastCache concept
/// Uses ConcurrentDictionary with Environment.TickCount64 for TTL tracking
/// </summary>
public class SimpleFastCache<TKey, TValue> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, CacheEntry> _cache = new();

    private readonly struct CacheEntry
    {
        public readonly TValue Value;
        public readonly long ExpiresAtTicks;

        public CacheEntry(TValue value, long expiresAtTicks)
        {
            Value = value;
            ExpiresAtTicks = expiresAtTicks;
        }

        public bool IsExpired => Environment.TickCount64 > ExpiresAtTicks;
    }

    public void Store(TKey key, TValue value, TimeSpan duration)
    {
        var expiresAt = Environment.TickCount64 + (long)duration.TotalMilliseconds;
        _cache[key] = new CacheEntry(value, expiresAt);
    }

    public TValue? Fetch(TKey key)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            if (!entry.IsExpired)
            {
                return entry.Value;
            }

            // Remove expired entry
            _cache.TryRemove(key, out _);
        }

        return default;
    }

    public void Invalidate(TKey key)
    {
        _cache.TryRemove(key, out _);
    }

    public void Clear()
    {
        _cache.Clear();
    }
}
