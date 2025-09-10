using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.HybridCache.Abstractions;
using MethodCache.HybridCache.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MethodCache.HybridCache.Implementation
{
    /// <summary>
    /// In-memory implementation of the L1 cache.
    /// </summary>
    public class MemoryL1Cache : IL1Cache
    {
        private readonly ConcurrentDictionary<string, L1CacheEntry> _cache;
        private readonly HybridCacheOptions _options;
        private readonly ILogger<MemoryL1Cache> _logger;
        private readonly Timer _cleanupTimer;
        private readonly SemaphoreSlim _evictionSemaphore;
        
        // Statistics
        private long _hits;
        private long _misses;
        private long _evictions;
        
        public MemoryL1Cache(IOptions<HybridCacheOptions> options, ILogger<MemoryL1Cache> logger)
        {
            _options = options.Value;
            _logger = logger;
            _cache = new ConcurrentDictionary<string, L1CacheEntry>();
            _evictionSemaphore = new SemaphoreSlim(1, 1);
            
            // Start cleanup timer for expired entries
            _cleanupTimer = new Timer(CleanupExpiredEntries, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        public Task<T?> GetAsync<T>(string key)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                if (entry.IsExpired)
                {
                    _cache.TryRemove(key, out _);
                    Interlocked.Increment(ref _misses);
                    return Task.FromResult<T?>(default);
                }

                entry.UpdateAccess();
                Interlocked.Increment(ref _hits);
                
                try
                {
                    return Task.FromResult<T?>((T)entry.Value);
                }
                catch (InvalidCastException)
                {
                    _logger.LogWarning("Type mismatch for key {Key}. Expected {ExpectedType}, got {ActualType}", 
                        key, typeof(T), entry.Value?.GetType());
                    Interlocked.Increment(ref _misses);
                    return Task.FromResult<T?>(default);
                }
            }

            Interlocked.Increment(ref _misses);
            return Task.FromResult<T?>(default);
        }

        public async Task SetAsync<T>(string key, T value, TimeSpan expiration)
        {
            if (value == null)
            {
                await RemoveAsync(key);
                return;
            }

            // Check if we need to evict entries to stay within limits
            if (_cache.Count >= _options.L1MaxItems)
            {
                await TryEvictAsync();
            }

            var effectiveExpiration = expiration > _options.L1MaxExpiration 
                ? _options.L1MaxExpiration 
                : expiration;

            var entry = new L1CacheEntry
            {
                Value = value!,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(effectiveExpiration),
                LastAccessedAt = DateTime.UtcNow
            };

            _cache.AddOrUpdate(key, entry, (k, oldEntry) => entry);
            
            _logger.LogTrace("Set key {Key} in L1 cache with expiration {Expiration}", key, effectiveExpiration);
        }

        public Task<bool> RemoveAsync(string key)
        {
            var removed = _cache.TryRemove(key, out _);
            if (removed)
            {
                _logger.LogTrace("Removed key {Key} from L1 cache", key);
            }
            return Task.FromResult(removed);
        }

        public Task ClearAsync()
        {
            var count = _cache.Count;
            _cache.Clear();
            _logger.LogDebug("Cleared L1 cache, removed {Count} entries", count);
            return Task.CompletedTask;
        }

        public Task<L1CacheStats> GetStatsAsync()
        {
            var stats = new L1CacheStats
            {
                Hits = _hits,
                Misses = _misses,
                Evictions = _evictions,
                Entries = _cache.Count,
                MemoryUsage = EstimateMemoryUsage()
            };

            return Task.FromResult(stats);
        }

        public Task<int> RemoveMultipleAsync(params string[] keys)
        {
            var removed = 0;
            foreach (var key in keys)
            {
                if (_cache.TryRemove(key, out _))
                {
                    removed++;
                }
            }
            
            if (removed > 0)
            {
                _logger.LogDebug("Removed {Count} keys from L1 cache", removed);
            }
            
            return Task.FromResult(removed);
        }

        public Task<bool> ExistsAsync(string key)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                if (entry.IsExpired)
                {
                    _cache.TryRemove(key, out _);
                    return Task.FromResult(false);
                }
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public void Dispose()
        {
            _cleanupTimer?.Dispose();
            _evictionSemaphore?.Dispose();
            _cache.Clear();
        }

        private async Task TryEvictAsync()
        {
            if (!await _evictionSemaphore.WaitAsync(100)) // Don't wait long for eviction lock
                return;

            try
            {
                var entriesToEvict = _options.L1EvictionPolicy switch
                {
                    L1EvictionPolicy.LRU => GetLRUEntries(),
                    L1EvictionPolicy.LFU => GetLFUEntries(),
                    L1EvictionPolicy.FIFO => GetFIFOEntries(),
                    L1EvictionPolicy.TTL => GetTTLEntries(),
                    _ => GetLRUEntries()
                };

                var evictCount = Math.Max(1, (int)(_options.L1MaxItems / 10)); // Evict 10% when full
                foreach (var key in entriesToEvict.Take(evictCount))
                {
                    if (_cache.TryRemove(key, out _))
                    {
                        Interlocked.Increment(ref _evictions);
                    }
                }
            }
            finally
            {
                _evictionSemaphore.Release();
            }
        }

        private IEnumerable<string> GetLRUEntries()
        {
            return _cache
                .OrderBy(kvp => kvp.Value.LastAccessedAt)
                .Select(kvp => kvp.Key);
        }

        private IEnumerable<string> GetLFUEntries()
        {
            return _cache
                .OrderBy(kvp => kvp.Value.AccessCount)
                .Select(kvp => kvp.Key);
        }

        private IEnumerable<string> GetFIFOEntries()
        {
            return _cache
                .OrderBy(kvp => kvp.Value.CreatedAt)
                .Select(kvp => kvp.Key);
        }

        private IEnumerable<string> GetTTLEntries()
        {
            return _cache
                .OrderBy(kvp => kvp.Value.ExpiresAt)
                .Select(kvp => kvp.Key);
        }

        private void CleanupExpiredEntries(object? state)
        {
            try
            {
                var expiredKeys = _cache
                    .Where(kvp => kvp.Value.IsExpired)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    _cache.TryRemove(key, out _);
                }

                if (expiredKeys.Count > 0)
                {
                    _logger.LogDebug("Cleaned up {Count} expired entries from L1 cache", expiredKeys.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during L1 cache cleanup");
            }
        }

        private long EstimateMemoryUsage()
        {
            // This is a rough estimate - in production you might want more accurate measurement
            const long overheadPerEntry = 100; // Estimated overhead per dictionary entry
            const long averageKeySize = 50;
            const long averageValueSize = 500; // This would need to be more sophisticated in production
            
            return _cache.Count * (overheadPerEntry + averageKeySize + averageValueSize);
        }

        private class L1CacheEntry
        {
            private long _accessCount;
            
            public object Value { get; init; } = null!;
            public DateTime CreatedAt { get; init; }
            public DateTime ExpiresAt { get; init; }
            public DateTime LastAccessedAt { get; set; }
            public long AccessCount => _accessCount;

            public bool IsExpired => DateTime.UtcNow > ExpiresAt;

            public void UpdateAccess()
            {
                LastAccessedAt = DateTime.UtcNow;
                Interlocked.Increment(ref _accessCount);
            }
        }
    }
}