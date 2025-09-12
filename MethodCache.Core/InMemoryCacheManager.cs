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
    /// Provides advanced features like eviction policies, statistics, and configurable memory usage calculation.
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
            public LinkedListNode<string>? OrderNode { get; set; } // For O(1) eviction tracking

            public bool IsExpired => DateTime.UtcNow > AbsoluteExpiration;

            public void UpdateAccess()
            {
                LastAccessedAt = DateTime.UtcNow;
                Interlocked.Increment(ref _accessCount);
            }
        }

        private readonly ConcurrentDictionary<string, EnhancedCacheEntry> _cache = new();
        private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _stampedePrevention = new();
        private readonly ICacheMetricsProvider _metricsProvider;
        private readonly MemoryCacheOptions _options;
        private readonly Timer? _cleanupTimer;
        private readonly SemaphoreSlim _evictionSemaphore;
        private readonly MemoryUsageCalculator _memoryCalculator;
        
        // O(1) eviction tracking
        private readonly LinkedList<string> _accessOrder = new();
        private readonly object _accessOrderLock = new object();
        
        // Tag reverse index for O(1) tag invalidation
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _tagToKeys = new();
        private readonly object _tagIndexLock = new object();
        private const int MaxTagMappings = 100000; // Prevent unbounded growth
        private int _currentTagMappings = 0;
        
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
            _memoryCalculator = new MemoryUsageCalculator(_options);
            
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

            // Check cache first using internal method to avoid double statistics
            var cachedResult = await GetAsyncInternal<T>(key, updateStatistics: false);
            if (cachedResult != null)
            {
                _metricsProvider.CacheHit(methodName);
                if (_options.EnableStatistics)
                {
                    Interlocked.Increment(ref _hits);
                }
                return cachedResult;
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
                    _metricsProvider.CacheError(methodName, ex.Message);
                    throw;
                }
            }));

            try
            {
                var finalResult = await lazyTask.Value.ConfigureAwait(false);
                
                _metricsProvider.CacheMiss(methodName);
                if (_options.EnableStatistics)
                {
                    Interlocked.Increment(ref _misses);
                }
                
                // Wait a moment to ensure cache is populated before removing stampede prevention
                // This prevents race condition where new request arrives before cache write completes
                await Task.Delay(1).ConfigureAwait(false);
                
                // Now safe to remove from stampede prevention
                _stampedePrevention.TryRemove(key, out _);
                
                return (T)finalResult;
            }
            catch
            {
                // On any error, remove the failed task to allow retries
                _stampedePrevention.TryRemove(key, out _);
                throw;
            }
        }

        public Task InvalidateByTagsAsync(params string[] tags)
        {
            if (tags == null || !tags.Any()) return Task.CompletedTask;
            
            var keysToRemove = new HashSet<string>();
            
            // Use O(1) reverse index lookup instead of iterating entire cache
            lock (_tagIndexLock)
            {
                foreach (var tag in tags)
                {
                    if (_tagToKeys.TryGetValue(tag, out var tagKeys))
                    {
                        foreach (var key in tagKeys.Keys)
                        {
                            keysToRemove.Add(key);
                        }
                    }
                }
            }
            
            // Remove all collected keys completely
            foreach (var key in keysToRemove)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    RemoveEntryCompletely(key, entry);
                }
            }
            
            return Task.CompletedTask;
        }

        #endregion

        #region IMemoryCache Implementation

        public Task<T?> GetAsync<T>(string key)
        {
            return GetAsyncInternal<T>(key, updateStatistics: true);
        }
        
        private Task<T?> GetAsyncInternal<T>(string key, bool updateStatistics)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                if (entry.IsExpired)
                {
                    RemoveEntryCompletely(key, entry);
                    if (updateStatistics && _options.EnableStatistics)
                    {
                        Interlocked.Increment(ref _misses);
                    }
                    return Task.FromResult<T?>(default);
                }

                // Update access tracking with O(1) LinkedList operation
                entry.UpdateAccess();
                UpdateAccessOrder(key, entry);
                
                if (updateStatistics && _options.EnableStatistics)
                {
                    Interlocked.Increment(ref _hits);
                }
                
                try
                {
                    return Task.FromResult<T?>((T)entry.Value);
                }
                catch (InvalidCastException)
                {
                    // Type mismatch - treat as miss but don't double count
                    if (updateStatistics && _options.EnableStatistics)
                    {
                        Interlocked.Increment(ref _misses);
                        // Compensate for the hit we incorrectly incremented
                        Interlocked.Decrement(ref _hits);
                    }
                    return Task.FromResult<T?>(default);
                }
            }

            if (updateStatistics && _options.EnableStatistics)
            {
                Interlocked.Increment(ref _misses);
            }
            return Task.FromResult<T?>(default);
        }
        
        /// <summary>
        /// Updates access order for eviction policies with O(1) LinkedList operations.
        /// </summary>
        private void UpdateAccessOrder(string key, EnhancedCacheEntry entry)
        {
            // Only update order for LRU policy (FIFO doesn't move on access)
            if (_options.EvictionPolicy != MemoryCacheEvictionPolicy.LRU) return;
            
            lock (_accessOrderLock)
            {
                if (entry.OrderNode != null)
                {
                    // Move existing node to head (most recently used)
                    _accessOrder.Remove(entry.OrderNode);
                    _accessOrder.AddFirst(entry.OrderNode);
                }
                else
                {
                    // Add new node to head
                    entry.OrderNode = _accessOrder.AddFirst(key);
                }
            }
        }
        
        /// <summary>
        /// Completely removes an entry from all data structures.
        /// </summary>
        private void RemoveEntryCompletely(string key, EnhancedCacheEntry entry)
        {
            _cache.TryRemove(key, out _);
            
            // Remove from access order tracking
            lock (_accessOrderLock)
            {
                if (entry.OrderNode != null)
                {
                    _accessOrder.Remove(entry.OrderNode);
                    entry.OrderNode = null;
                }
            }
            
            // Remove from tag index
            RemoveFromTagIndex(key, entry.Tags);
        }

        public async Task SetAsync<T>(string key, T value, TimeSpan expiration)
        {
            await SetAsync(key, value, expiration, Array.Empty<string>());
        }

        private async Task SetAsync<T>(string key, T value, TimeSpan expiration, string[] tags)
        {
            if (value == null)
            {
                await RemoveAsync(key);
                return;
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

            // Check if this is an update to existing entry
            var isUpdate = _cache.TryGetValue(key, out var oldEntry);
            
            // Only evict if this is a new entry and we're at capacity
            if (!isUpdate && _cache.Count >= _options.MaxItems)
            {
                await TryEvictAsync();
            }
            
            // Remove old entry completely if updating
            if (isUpdate && oldEntry != null)
            {
                RemoveEntryCompletely(key, oldEntry);
            }
            
            // Add the new entry to all data structures
            _cache[key] = entry;
            AddToAccessOrder(key, entry);
            AddToTagIndex(key, entry.Tags);
        }
        
        /// <summary>
        /// Adds entry to access order tracking with O(1) operation.
        /// </summary>
        private void AddToAccessOrder(string key, EnhancedCacheEntry entry)
        {
            lock (_accessOrderLock)
            {
                entry.OrderNode = _accessOrder.AddFirst(key);
            }
        }
        
        /// <summary>
        /// Adds entry to tag reverse index with size limit protection.
        /// </summary>
        private void AddToTagIndex(string key, HashSet<string> tags)
        {
            lock (_tagIndexLock)
            {
                // Check if we're approaching the limit
                if (_currentTagMappings >= MaxTagMappings)
                {
                    // Perform cleanup of stale entries
                    CleanupStaleTags();
                }
                
                foreach (var tag in tags)
                {
                    var tagKeys = _tagToKeys.GetOrAdd(tag, _ => new ConcurrentDictionary<string, byte>());
                    if (tagKeys.TryAdd(key, 0))
                    {
                        Interlocked.Increment(ref _currentTagMappings);
                    }
                }
            }
        }
        
        /// <summary>
        /// Removes entry from tag reverse index.
        /// </summary>
        private void RemoveFromTagIndex(string key, HashSet<string> tags)
        {
            lock (_tagIndexLock)
            {
                foreach (var tag in tags)
                {
                    if (_tagToKeys.TryGetValue(tag, out var tagKeys))
                    {
                        if (tagKeys.TryRemove(key, out _))
                        {
                            Interlocked.Decrement(ref _currentTagMappings);
                        }
                        // Clean up empty tag entries
                        if (tagKeys.IsEmpty)
                        {
                            _tagToKeys.TryRemove(tag, out _);
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// Cleanup stale tag mappings to prevent memory leaks.
        /// </summary>
        private void CleanupStaleTags()
        {
            var keysToRemove = new List<string>();
            
            // Check all tag mappings and remove entries that no longer exist in cache
            foreach (var tagEntry in _tagToKeys)
            {
                foreach (var key in tagEntry.Value.Keys)
                {
                    if (!_cache.ContainsKey(key))
                    {
                        keysToRemove.Add(key);
                    }
                }
                
                // Remove stale keys from this tag
                foreach (var key in keysToRemove)
                {
                    if (tagEntry.Value.TryRemove(key, out _))
                    {
                        Interlocked.Decrement(ref _currentTagMappings);
                    }
                }
                
                keysToRemove.Clear();
                
                // Remove empty tag entries
                if (tagEntry.Value.IsEmpty)
                {
                    _tagToKeys.TryRemove(tagEntry.Key, out _);
                }
            }
        }

        public Task<bool> RemoveAsync(string key)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                RemoveEntryCompletely(key, entry);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task ClearAsync()
        {
            _cache.Clear();
            _stampedePrevention.Clear();
            
            // Clear all tracking structures
            lock (_accessOrderLock)
            {
                _accessOrder.Clear();
            }
            
            lock (_tagIndexLock)
            {
                _tagToKeys.Clear();
            }
            
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
                if (_cache.TryGetValue(key, out var entry))
                {
                    RemoveEntryCompletely(key, entry);
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
                    RemoveEntryCompletely(key, entry);
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
                var currentCount = _cache.Count;
                if (currentCount <= _options.MaxItems)
                {
                    return; // No eviction needed
                }
                
                // Calculate how many entries to evict
                var targetCount = (int)(_options.MaxItems * 0.9); // Target 90% capacity
                var evictCount = Math.Max(1, currentCount - targetCount);
                evictCount = Math.Min(evictCount, (int)(_options.MaxItems * 0.2)); // Never evict more than 20%
                
                int actualEvicted = 0;
                
                // Use appropriate eviction algorithm based on policy
                switch (_options.EvictionPolicy)
                {
                    case MemoryCacheEvictionPolicy.LRU:
                    case MemoryCacheEvictionPolicy.FIFO:
                        actualEvicted = EvictFromAccessOrderO1(evictCount);
                        break;
                        
                    case MemoryCacheEvictionPolicy.LFU:
                        actualEvicted = EvictLFUApproximate(evictCount);
                        break;
                        
                    case MemoryCacheEvictionPolicy.LFU_Precise:
                        actualEvicted = EvictLFUPrecise(evictCount);
                        break;
                        
                    case MemoryCacheEvictionPolicy.TTL:
                        actualEvicted = EvictTTLApproximate(evictCount);
                        break;
                        
                    case MemoryCacheEvictionPolicy.TTL_Precise:
                        actualEvicted = EvictTTLPrecise(evictCount);
                        break;
                        
                    default:
                        actualEvicted = EvictFromAccessOrderO1(evictCount);
                        break;
                }
                
                if (_options.EnableStatistics)
                {
                    Interlocked.Add(ref _evictions, actualEvicted);
                }
            }
            finally
            {
                _evictionSemaphore.Release();
            }
        }
        
        /// <summary>
        /// O(1) eviction for LRU and FIFO policies using LinkedList.
        /// </summary>
        private int EvictFromAccessOrderO1(int maxEvictions)
        {
            int evicted = 0;
            
            lock (_accessOrderLock)
            {
                for (int i = 0; i < maxEvictions && _accessOrder.Count > 0; i++)
                {
                    var lastNode = _accessOrder.Last;
                    if (lastNode == null) break;
                    
                    var keyToEvict = lastNode.Value;
                    
                    // Remove from access order first
                    _accessOrder.RemoveLast();
                    
                    // Try to remove from cache
                    if (_cache.TryRemove(keyToEvict, out var entry))
                    {
                        entry.OrderNode = null; // Clear the reference
                        RemoveFromTagIndex(keyToEvict, entry.Tags);
                        evicted++;
                    }
                }
            }
            
            return evicted;
        }
        
        /// <summary>
        /// Approximate LFU eviction using sampling for better performance.
        /// Trades precision for speed - may not evict the globally least frequently used item.
        /// </summary>
        private int EvictLFUApproximate(int maxEvictions)
        {
            var totalItems = _cache.Count;
            var sampleSize = Math.Max(maxEvictions, (int)(totalItems * _options.EvictionSamplePercentage));
            sampleSize = Math.Min(sampleSize, totalItems);
            
            var candidates = _cache.Take(sampleSize)
                .OrderBy(kvp => kvp.Value.AccessCount)
                .Take(maxEvictions)
                .Select(kvp => kvp.Key)
                .ToList();
            
            int evicted = 0;
            foreach (var key in candidates)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    RemoveEntryCompletely(key, entry);
                    evicted++;
                }
            }
            
            return evicted;
        }
        
        /// <summary>
        /// Precise LFU eviction - guarantees eviction of globally least frequently used items.
        /// WARNING: O(N log N) performance - expensive for large caches.
        /// </summary>
        private int EvictLFUPrecise(int maxEvictions)
        {
            var candidates = _cache
                .OrderBy(kvp => kvp.Value.AccessCount)
                .ThenBy(kvp => kvp.Value.LastAccessedAt) // Tiebreaker: older access wins
                .Take(maxEvictions)
                .Select(kvp => kvp.Key)
                .ToList();
            
            int evicted = 0;
            foreach (var key in candidates)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    RemoveEntryCompletely(key, entry);
                    evicted++;
                }
            }
            
            return evicted;
        }
        
        /// <summary>
        /// Approximate TTL eviction using sampling for better performance.
        /// Trades precision for speed - may not evict the item globally closest to expiration.
        /// </summary>
        private int EvictTTLApproximate(int maxEvictions)
        {
            var totalItems = _cache.Count;
            var sampleSize = Math.Max(maxEvictions, (int)(totalItems * _options.EvictionSamplePercentage));
            sampleSize = Math.Min(sampleSize, totalItems);
            
            var candidates = _cache.Take(sampleSize)
                .OrderBy(kvp => kvp.Value.AbsoluteExpiration)
                .Take(maxEvictions)
                .Select(kvp => kvp.Key)
                .ToList();
            
            int evicted = 0;
            foreach (var key in candidates)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    RemoveEntryCompletely(key, entry);
                    evicted++;
                }
            }
            
            return evicted;
        }
        
        /// <summary>
        /// Precise TTL eviction - guarantees eviction of items globally closest to expiration.
        /// WARNING: O(N log N) performance - expensive for large caches.
        /// </summary>
        private int EvictTTLPrecise(int maxEvictions)
        {
            var candidates = _cache
                .OrderBy(kvp => kvp.Value.AbsoluteExpiration)
                .ThenBy(kvp => kvp.Value.CreatedAt) // Tiebreaker: older creation wins
                .Take(maxEvictions)
                .Select(kvp => kvp.Key)
                .ToList();
            
            int evicted = 0;
            foreach (var key in candidates)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    RemoveEntryCompletely(key, entry);
                    evicted++;
                }
            }
            
            return evicted;
        }


        private void CleanupExpiredEntries(object? state)
        {
            try
            {
                var expiredKeys = new List<string>();
                const int maxBatchSize = 1000; // Process in batches to avoid long pauses
                int processedCount = 0;
                
                // Use sampling approach for large caches instead of full enumeration
                var totalCount = _cache.Count;
                var sampleSize = Math.Min(totalCount, maxBatchSize);
                
                if (totalCount <= maxBatchSize)
                {
                    // Small cache - check all entries
                    foreach (var kvp in _cache)
                    {
                        if (kvp.Value.IsExpired)
                        {
                            expiredKeys.Add(kvp.Key);
                        }
                        
                        if (++processedCount >= maxBatchSize)
                            break;
                    }
                }
                else
                {
                    // Large cache - use sampling to avoid performance impact
                    var sample = _cache.Take(sampleSize);
                    foreach (var kvp in sample)
                    {
                        if (kvp.Value.IsExpired)
                        {
                            expiredKeys.Add(kvp.Key);
                        }
                    }
                }

                // Use proper removal method that cleans up all data structures
                foreach (var key in expiredKeys)
                {
                    if (_cache.TryGetValue(key, out var entry) && entry.IsExpired)
                    {
                        RemoveEntryCompletely(key, entry);
                    }
                }
                
                // If we found many expired entries in our sample, schedule another cleanup soon
                if (expiredKeys.Count > sampleSize * 0.5 && totalCount > maxBatchSize)
                {
                    // High expiration rate detected - schedule more frequent cleanup
                    _cleanupTimer?.Change(TimeSpan.FromSeconds(10), _options.CleanupInterval);
                }
            }
            catch (Exception ex)
            {
                // Log the exception if possible, but don't crash the application
                // In a real implementation, we'd want to log this
                System.Diagnostics.Debug.WriteLine($"Error in background cleanup: {ex.Message}");
            }
        }

        /// <summary>
        /// Estimates memory usage using the configured calculation mode.
        /// </summary>
        private long EstimateMemoryUsage()
        {
            return _memoryCalculator.CalculateMemoryUsage(_cache, entry => entry.Value);
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
