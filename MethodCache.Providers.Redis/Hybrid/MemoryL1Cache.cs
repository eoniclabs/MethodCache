using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MethodCache.Providers.Redis.Hybrid
{
    public class MemoryL1Cache : IL1Cache, IDisposable
    {
        private readonly ConcurrentDictionary<string, L1CacheEntry<object>> _cache;
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
            _cache = new ConcurrentDictionary<string, L1CacheEntry<object>>();
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
                await TryEvictLRUAsync();
            }

            var entry = new L1CacheEntry<object>
            {
                Value = value,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(expiration),
                LastAccessedAt = DateTime.UtcNow,
                AccessCount = 0,
                SlidingExpiration = _options.L1SlidingExpiration,
                SlidingWindow = expiration
            };

            _cache.AddOrUpdate(key, entry, (k, oldEntry) => entry);
            
            _logger.LogTrace("Set key {Key} in L1 cache with expiration {Expiration}", key, expiration);
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

        public Task<IEnumerable<string>> GetKeysAsync(string pattern = "*")
        {
            // Simple pattern matching - in production, you might want more sophisticated pattern matching
            if (pattern == "*")
            {
                return Task.FromResult<IEnumerable<string>>(_cache.Keys);
            }

            var regex = new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$");
            
            var matchingKeys = _cache.Keys.Where(key => regex.IsMatch(key));
            return Task.FromResult(matchingKeys);
        }

        public Task<long> GetCountAsync()
        {
            return Task.FromResult((long)_cache.Count);
        }

        public Task<L1CacheStats> GetStatsAsync()
        {
            var stats = new L1CacheStats
            {
                Hits = _hits,
                Misses = _misses,
                Evictions = _evictions,
                Entries = _cache.Count,
                MemoryUsageBytes = EstimateMemoryUsage()
            };

            return Task.FromResult(stats);
        }

        public Task EvictExpiredAsync()
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
                _logger.LogDebug("Evicted {Count} expired entries from L1 cache", expiredKeys.Count);
            }

            return Task.CompletedTask;
        }

        public async Task<bool> TryEvictLRUAsync()
        {
            if (!await _evictionSemaphore.WaitAsync(100)) // Don't wait long for eviction lock
                return false;

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

                var evicted = false;
                foreach (var (key, _) in entriesToEvict.Take(Math.Max(1, (int)(_options.L1MaxItems / 10)))) // Evict 10% when full
                {
                    if (_cache.TryRemove(key, out _))
                    {
                        Interlocked.Increment(ref _evictions);
                        evicted = true;
                    }
                }

                if (evicted)
                {
                    _logger.LogDebug("Evicted entries from L1 cache using {Policy} policy", _options.L1EvictionPolicy);
                }

                return evicted;
            }
            finally
            {
                _evictionSemaphore.Release();
            }
        }

        private IEnumerable<KeyValuePair<string, L1CacheEntry<object>>> GetLRUEntries()
        {
            return _cache.OrderBy(kvp => kvp.Value.LastAccessedAt);
        }

        private IEnumerable<KeyValuePair<string, L1CacheEntry<object>>> GetLFUEntries()
        {
            return _cache.OrderBy(kvp => kvp.Value.AccessCount);
        }

        private IEnumerable<KeyValuePair<string, L1CacheEntry<object>>> GetFIFOEntries()
        {
            return _cache.OrderBy(kvp => kvp.Value.CreatedAt);
        }

        private IEnumerable<KeyValuePair<string, L1CacheEntry<object>>> GetTTLEntries()
        {
            return _cache.OrderBy(kvp => kvp.Value.ExpiresAt);
        }

        private long EstimateMemoryUsage()
        {
            // Rough estimation - in production you might want more accurate measurement
            return _cache.Count * 1024; // Assume ~1KB per entry on average
        }

        private async void CleanupExpiredEntries(object? state)
        {
            try
            {
                await EvictExpiredAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during L1 cache cleanup");
            }
        }

        public void Dispose()
        {
            _cleanupTimer?.Dispose();
            _evictionSemaphore?.Dispose();
            _cache?.Clear();
        }
    }
}