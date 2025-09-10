using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Core.Configuration;
using Microsoft.Extensions.Options;

namespace MethodCache.Core
{
    /// <summary>
    /// Enhanced in-memory cache manager that implements both ICacheManager and IMemoryCache interfaces.
    /// Provides advanced features like eviction policies, statistics, and background cleanup.
    /// </summary>
    public class InMemoryCacheManager : ICacheManager, IMemoryCache
    {
        private class EnhancedCacheEntry
        {
            private long _accessCount;
            
            public object Value { get; init; } = null!;
            public HashSet<string> Tags { get; init; } = new HashSet<string>();
            public DateTimeOffset AbsoluteExpiration { get; init; }
            public DateTime CreatedAt { get; init; }
            public DateTime LastAccessedAt { get; set; }
            public long AccessCount => _accessCount;

            public bool IsExpired => DateTime.UtcNow > AbsoluteExpiration;

            public void UpdateAccess()
            {
                LastAccessedAt = DateTime.UtcNow;
                Interlocked.Increment(ref _accessCount);
            }
        }

        private readonly ConcurrentDictionary<string, EnhancedCacheEntry> _cache = new ConcurrentDictionary<string, EnhancedCacheEntry>();
        private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _stampedePrevention = new ConcurrentDictionary<string, Lazy<Task<object>>>();
        private readonly ICacheMetricsProvider _metricsProvider;
        private readonly MemoryCacheOptions _options;
        private readonly Timer? _cleanupTimer;
        private readonly SemaphoreSlim _evictionSemaphore;
        
        // Statistics
        private long _hits;
        private long _misses;
        private long _evictions;
        private bool _disposed = false;

        public InMemoryCacheManager(ICacheMetricsProvider metricsProvider, IOptions<MemoryCacheOptions>? options = null)
        {
            _metricsProvider = metricsProvider;
            _options = options?.Value ?? new MemoryCacheOptions();
            _evictionSemaphore = new SemaphoreSlim(1, 1);
            
            // Start cleanup timer for expired entries if enabled
            if (_options.EnableBackgroundCleanup)
            {
                _cleanupTimer = new Timer(CleanupExpiredEntries, null, _options.CleanupInterval, _options.CleanupInterval);
            }
        }

        #region ICacheManager Implementation

        public async Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator, bool requireIdempotent)
        {
            var key = keyGenerator.GenerateKey(methodName, args, settings);

            // Check cache first - fast memory operation
            if (_cache.TryGetValue(key, out var entry) && !entry.IsExpired)
            {
                entry.UpdateAccess();
                _metricsProvider.CacheHit(methodName);
                if (_options.EnableStatistics)
                {
                    Interlocked.Increment(ref _hits);
                }
                return (T)entry.Value;
            }

            // Remove expired entry
            if (entry?.IsExpired == true)
            {
                _cache.TryRemove(key, out _);
            }

            // Validate idempotency requirement
            if (requireIdempotent && !settings.IsIdempotent)
            {
                throw new InvalidOperationException($"Method {methodName} is not marked as idempotent, but caching requires it.");
            }

            // Handle cache miss with stampede prevention
            var lazyTask = _stampedePrevention.GetOrAdd(key, _ => new Lazy<Task<object>>(async () =>
            {
                try
                {
                    // Let the factory method run without timeout - service layer handles resilience
                    var result = await factory().ConfigureAwait(false);
                    
                    if (result != null)
                    {
                        var expiration = settings.Duration ?? _options.DefaultExpiration;
                        var effectiveExpiration = expiration > _options.MaxExpiration 
                            ? _options.MaxExpiration 
                            : expiration;

                        await SetAsync(key, result, effectiveExpiration, settings.Tags?.ToArray() ?? Array.Empty<string>());
                    }
                    return result!;
                }
                catch (Exception ex)
                {
                    // Log the error but let it propagate - service layer should handle retries/fallbacks
                    _metricsProvider.CacheError(methodName, ex.Message);
                    throw;
                }
            }));

            var finalResult = await lazyTask.Value.ConfigureAwait(false);
            _stampedePrevention.TryRemove(key, out _); // Clean up after task completes

            _metricsProvider.CacheMiss(methodName);
            if (_options.EnableStatistics)
            {
                Interlocked.Increment(ref _misses);
            }
            
            return (T)finalResult;
        }

        public Task InvalidateByTagsAsync(params string[] tags)
        {
            foreach (var tag in tags)
            {
                var keysToRemove = _cache.Where(kvp => kvp.Value.Tags.Contains(tag)).Select(kvp => kvp.Key).ToList();
                foreach (var key in keysToRemove)
                {
                    _cache.TryRemove(key, out _);
                }
            }
            return Task.CompletedTask;
        }

        #endregion

        #region IMemoryCache Implementation

        public Task<T?> GetAsync<T>(string key)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                if (entry.IsExpired)
                {
                    _cache.TryRemove(key, out _);
                    if (_options.EnableStatistics)
                    {
                        Interlocked.Increment(ref _misses);
                    }
                    return Task.FromResult<T?>(default);
                }

                entry.UpdateAccess();
                if (_options.EnableStatistics)
                {
                    Interlocked.Increment(ref _hits);
                }
                
                try
                {
                    return Task.FromResult<T?>((T)entry.Value);
                }
                catch (InvalidCastException)
                {
                    // Type mismatch - treat as miss
                    if (_options.EnableStatistics)
                    {
                        Interlocked.Increment(ref _misses);
                    }
                    return Task.FromResult<T?>(default);
                }
            }

            if (_options.EnableStatistics)
            {
                Interlocked.Increment(ref _misses);
            }
            return Task.FromResult<T?>(default);
        }

        public async Task SetAsync<T>(string key, T value, TimeSpan expiration)
        {
            await SetAsync(key, value, expiration, new string[0]);
        }

        private async Task SetAsync<T>(string key, T value, TimeSpan expiration, string[] tags)
        {
            if (value == null)
            {
                await RemoveAsync(key);
                return;
            }

            // Check if we need to evict entries to stay within limits
            if (_cache.Count >= _options.MaxItems)
            {
                await TryEvictAsync();
            }

            var effectiveExpiration = expiration > _options.MaxExpiration 
                ? _options.MaxExpiration 
                : expiration;

            var entry = new EnhancedCacheEntry
            {
                Value = value!,
                Tags = new HashSet<string>(tags),
                CreatedAt = DateTime.UtcNow,
                AbsoluteExpiration = DateTimeOffset.UtcNow.Add(effectiveExpiration),
                LastAccessedAt = DateTime.UtcNow
            };

            _cache.AddOrUpdate(key, entry, (k, oldEntry) => entry);
        }

        public Task<bool> RemoveAsync(string key)
        {
            var removed = _cache.TryRemove(key, out _);
            return Task.FromResult(removed);
        }

        public Task ClearAsync()
        {
            _cache.Clear();
            _stampedePrevention.Clear();
            
            // Reset statistics
            if (_options.EnableStatistics)
            {
                Interlocked.Exchange(ref _hits, 0);
                Interlocked.Exchange(ref _misses, 0);
                Interlocked.Exchange(ref _evictions, 0);
            }
            
            return Task.CompletedTask;
        }

        public Task<ICacheStats> GetStatsAsync()
        {
            var stats = new CacheStats
            {
                Hits = _hits,
                Misses = _misses,
                Evictions = _evictions,
                Entries = _cache.Count,
                MemoryUsage = EstimateMemoryUsage()
            };

            return Task.FromResult<ICacheStats>(stats);
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

        #endregion

        #region Advanced Features

        private async Task TryEvictAsync()
        {
            if (!await _evictionSemaphore.WaitAsync(100)) // Don't wait long for eviction lock
                return;

            try
            {
                var entriesToEvict = _options.EvictionPolicy switch
                {
                    MemoryCacheEvictionPolicy.LRU => GetLRUEntries(),
                    MemoryCacheEvictionPolicy.LFU => GetLFUEntries(),
                    MemoryCacheEvictionPolicy.FIFO => GetFIFOEntries(),
                    MemoryCacheEvictionPolicy.TTL => GetTTLEntries(),
                    _ => GetLRUEntries()
                };

                var evictCount = Math.Max(1, (int)(_options.MaxItems / 10)); // Evict 10% when full
                foreach (var key in entriesToEvict.Take(evictCount))
                {
                    if (_cache.TryRemove(key, out _))
                    {
                        if (_options.EnableStatistics)
                        {
                            Interlocked.Increment(ref _evictions);
                        }
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
                .OrderBy(kvp => kvp.Value.AbsoluteExpiration)
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
            }
            catch (Exception)
            {
                // Swallow exceptions in background cleanup to avoid crashing the application
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

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (!_disposed)
            {
                _cleanupTimer?.Dispose();
                _evictionSemaphore?.Dispose();
                _cache.Clear();
                _stampedePrevention.Clear();
                _disposed = true;
            }
        }

        #endregion
    }
}