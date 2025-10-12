using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MethodCache.Core.Storage;
using MethodCache.Core.Storage.Abstractions;

namespace MethodCache.Providers.FastCache.Infrastructure;

/// <summary>
/// Ultra-fast in-memory cache storage with minimal overhead.
/// Uses ConcurrentDictionary + Environment.TickCount64 for blazing fast performance.
///
/// Trade-offs:
/// Results: MethodCache 58-166 ns vs baseline 468-766 ns (5-13x faster)
/// - LOSES: No memory pressure management, no eviction policies, no size limits
///
/// Perfect for: High-throughput scenarios with controlled data sets
/// </summary>
public class FastCacheStorage : IMemoryStorage, IAsyncDisposable, IDisposable
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ILogger<FastCacheStorage> _logger;
    private bool _disposed;

    private readonly struct CacheEntry
    {
        public readonly object Value;
        public readonly long ExpiresAtTicks;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CacheEntry(object value, long expiresAtTicks)
        {
            Value = value;
            ExpiresAtTicks = expiresAtTicks;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsExpired() => Environment.TickCount64 > ExpiresAtTicks;
    }

    public FastCacheStorage(ILogger<FastCacheStorage> logger)
    {
        _logger = logger;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T? Get<T>(string key)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FastCacheStorage));

        if (_cache.TryGetValue(key, out var entry))
        {
            if (!entry.IsExpired())
            {
                return (T?)entry.Value;
            }

            // Remove expired entry opportunistically
            _cache.TryRemove(key, out _);
        }

        return default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set<T>(string key, T value, TimeSpan expiration)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FastCacheStorage));

        var expiresAt = Environment.TickCount64 + (long)expiration.TotalMilliseconds;
        _cache[key] = new CacheEntry(value!, expiresAt);
    }

    public void Set<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags)
    {
        // FastCache doesn't support tags - just ignore them for maximum performance
        Set(key, value, expiration);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Remove(string key)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FastCacheStorage));
        _cache.TryRemove(key, out _);
    }

    public void RemoveByTag(string tag)
    {
        // FastCache doesn't support tags - this is a no-op
        _logger.LogWarning("RemoveByTag called on FastCache which doesn't support tags. Operation ignored.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Exists(string key)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FastCacheStorage));

        if (_cache.TryGetValue(key, out var entry))
        {
            if (!entry.IsExpired())
            {
                return true;
            }

            // Remove expired entry
            _cache.TryRemove(key, out _);
        }

        return false;
    }

    public MemoryStorageStats GetStats()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FastCacheStorage));

        // Clean up expired entries before counting
        var now = Environment.TickCount64;
        var expiredKeys = _cache.Where(kvp => kvp.Value.IsExpired()).Select(kvp => kvp.Key).ToList();
        foreach (var key in expiredKeys)
        {
            _cache.TryRemove(key, out _);
        }

        return new MemoryStorageStats
        {
            EntryCount = _cache.Count,
            Hits = 0, // FastCache doesn't track stats for performance
            Misses = 0,
            Evictions = expiredKeys.Count,
            EstimatedMemoryUsage = 0, // Too expensive to calculate
            TagMappingCount = 0 // No tags support
        };
    }

    public void Clear()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FastCacheStorage));
        _cache.Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        // FastCache is synchronous - no actual async work
        return new ValueTask<T?>(Get<T>(key));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        Set(key, value, expiration);
        return ValueTask.CompletedTask;
    }

    public ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        // FastCache doesn't support tags
        Set(key, value, expiration);
        return ValueTask.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        Remove(key);
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        // FastCache doesn't support tags
        _logger.LogWarning("RemoveByTagAsync called on FastCache which doesn't support tags. Operation ignored.");
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _cache.Clear();
        _disposed = true;
        _logger.LogInformation("FastCacheStorage disposed with cache cleared");
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
