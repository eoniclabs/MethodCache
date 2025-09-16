using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.HybridCache.Abstractions;
using MethodCache.HybridCache.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MethodCache.HybridCache.Implementation
{
    /// <summary>
    /// Implements a hybrid caching strategy with L1 (in-memory) and L2 (distributed) caches.
    /// </summary>
    public class HybridCacheManager : IHybridCacheManager, IAsyncDisposable
    {
        private readonly IMemoryCache _l1Cache;
        private readonly ICacheManager? _l2Cache;
        private readonly ICacheBackplane? _backplane;
        private readonly HybridCacheOptions _options;
        private readonly ILogger<HybridCacheManager> _logger;
        private readonly SemaphoreSlim _l2Semaphore;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLevelLocks;
        
        // Tag tracking infrastructure for efficient L1 invalidation
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _tagToKeys;
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _keyToTags;
        private readonly ReaderWriterLockSlim _tagMappingLock;
        private volatile int _tagMappingCount;
        
        // Statistics
        private long _l1Hits;
        private long _l1Misses;
        private long _l2Hits;
        private long _l2Misses;
        private long _backplaneMessagesSent;
        private long _backplaneMessagesReceived;
        
        // Cached objects for GetL2ValueDirectlyAsync optimization
        private static readonly CacheMethodSettings CachedL2Settings = new() { Duration = TimeSpan.FromMinutes(1) };
        private static readonly object[] EmptyArgs = Array.Empty<object>();
        private const string L2DirectGetMethodName = "HybridL2DirectGet";
        
        // Disposal tracking
        private bool _disposed = false;
        
        // Helper properties for cleaner logic
        private bool ShouldUseL2 => _options.L2Enabled && _options.Strategy != HybridStrategy.L1Only;
        private bool ShouldUseL1 => _options.Strategy != HybridStrategy.L2Only;
        private bool IsL1OnlyMode => _options.Strategy == HybridStrategy.L1Only;

        public HybridCacheManager(
            IMemoryCache l1Cache,
            ICacheManager? l2Cache,
            ICacheBackplane? backplane,
            IOptions<HybridCacheOptions> options,
            ILogger<HybridCacheManager> logger)
        {
            _l1Cache = l1Cache ?? throw new ArgumentNullException(nameof(l1Cache));
            _l2Cache = l2Cache; // Null is acceptable for L1-only scenarios
            _backplane = backplane;
            _options = options.Value;
            _logger = logger;
            
            // Validate L2Cache dependency based on configuration
            if (_options.L2Enabled && _l2Cache == null)
            {
                throw new InvalidOperationException("L2 cache is enabled but no ICacheManager was provided for L2 operations.");
            }
            
            _l2Semaphore = new SemaphoreSlim(_options.MaxConcurrentL2Operations, _options.MaxConcurrentL2Operations);
            _keyLevelLocks = new ConcurrentDictionary<string, SemaphoreSlim>();
            
            // Initialize tag tracking infrastructure
            _tagToKeys = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>();
            _keyToTags = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>();
            _tagMappingLock = new ReaderWriterLockSlim();
            _tagMappingCount = 0;
            
            // Subscribe to backplane invalidation events if available
            if (_backplane != null && _options.EnableBackplane)
            {
                _backplane.InvalidationReceived += OnBackplaneInvalidationReceived;
                _ = StartBackplaneListeningAsync();
                _logger.LogInformation("Hybrid cache backplane enabled for instance {InstanceId}", _options.InstanceId);
            }
        }

        private async Task StartBackplaneListeningAsync()
        {
            const int maxRetries = 3;
            const int baseDelayMs = 1000;
            
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    await _backplane!.StartListeningAsync();
                    _logger.LogInformation("Successfully started backplane listening on attempt {Attempt}", attempt);
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt == maxRetries)
                    {
                        _logger.LogError(ex, "Failed to start backplane listening after {MaxRetries} attempts", maxRetries);
                        return;
                    }
                    
                    var delayMs = baseDelayMs * (int)Math.Pow(2, attempt - 1); // Exponential backoff
                    _logger.LogWarning(ex, "Failed to start backplane listening on attempt {Attempt}, retrying in {DelayMs}ms", 
                        attempt, delayMs);
                    
                    await Task.Delay(delayMs);
                }
            }
        }

        public async Task<T> GetOrCreateAsync<T>(
            string methodName, 
            object[] args, 
            Func<Task<T>> factory, 
            CacheMethodSettings settings, 
            ICacheKeyGenerator keyGenerator, 
            bool requireIdempotent)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            
            var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);
            
            // Check L1 cache first
            var l1Value = await _l1Cache.GetAsync<T>(cacheKey);
            if (l1Value != null)
            {
                Interlocked.Increment(ref _l1Hits);
                _logger.LogTrace("L1 cache hit for key {Key}", cacheKey);
                return l1Value;
            }
            
            Interlocked.Increment(ref _l1Misses);
            
            // Use key-level locking to prevent multiple threads from executing the same factory
            var keyLock = _keyLevelLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            
            await keyLock.WaitAsync();
            try
            {
                // Double-check L1 cache after acquiring lock
                l1Value = await _l1Cache.GetAsync<T>(cacheKey);
                if (l1Value != null)
                {
                    Interlocked.Increment(ref _l1Hits);
                    Interlocked.Decrement(ref _l1Misses); // Adjust since we incremented above
                    _logger.LogTrace("L1 cache hit after lock for key {Key}", cacheKey);
                    return l1Value;
                }
                
                // Try L2 cache if enabled
                if (ShouldUseL2)
                {
                    try
                    {
                        await _l2Semaphore.WaitAsync();
                        
                        // Check if value exists in L2 first
                        var (l2Exists, l2Value) = await GetL2ValueDirectlyAsync<T>(cacheKey);
                        if (l2Exists)
                        {
                            // L2 cache hit - warm L1 and return
                            await StoreInL1IfEnabled(cacheKey, l2Value, settings);
                            
                            Interlocked.Increment(ref _l2Hits);
                            _logger.LogTrace("L2 cache hit for key {Key}, warmed L1 cache", cacheKey);
                            return l2Value;
                        }
                        
                        // L2 cache miss - fall through to factory execution
                        Interlocked.Increment(ref _l2Misses);
                    }
                    finally
                    {
                        _l2Semaphore.Release();
                    }
                }
                
                // Execute factory and store in appropriate cache layers
                // This handles: L2 miss, L1-only mode, and L2-disabled scenarios
                return await ExecuteFactoryAndCache(methodName, args, factory, settings, keyGenerator, requireIdempotent, cacheKey);
            }
            finally
            {
                keyLock.Release();
                
                // Clean up key-level lock if no other threads are waiting
                // Use atomic operations to safely remove and dispose the semaphore
                if (keyLock.CurrentCount == 1)
                {
                    // Use CompareExchange-style removal to prevent race conditions
                    // Only remove if the semaphore in the dictionary is still the same instance
                    var keyValuePair = new KeyValuePair<string, SemaphoreSlim>(cacheKey, keyLock);
                    if (((ICollection<KeyValuePair<string, SemaphoreSlim>>)_keyLevelLocks).Remove(keyValuePair))
                    {
                        // Successfully removed the exact semaphore we were using
                        keyLock.Dispose();
                    }
                    // If removal failed, another thread replaced the semaphore, so leave it alone
                }
            }
        }
        
        /// <summary>
        /// Helper method to store a value in L1 cache if enabled by strategy.
        /// </summary>
        private async Task StoreInL1IfEnabled<T>(string cacheKey, T value, CacheMethodSettings settings)
        {
            if (ShouldUseL1 && value != null)
            {
                var l1Expiration = CalculateL1Expiration(settings);
                await _l1Cache.SetAsync(cacheKey, value, l1Expiration);
                
                // Track tags for efficient invalidation
                TrackKeyTags(cacheKey, settings.Tags);
            }
        }
        
        /// <summary>
        /// Helper method to execute factory and store result in appropriate cache layers.
        /// Handles L2 miss, L1-only mode, and L2-disabled scenarios.
        /// </summary>
        private async Task<T> ExecuteFactoryAndCache<T>(
            string methodName, 
            object[] args, 
            Func<Task<T>> factory, 
            CacheMethodSettings settings, 
            ICacheKeyGenerator keyGenerator, 
            bool requireIdempotent, 
            string cacheKey)
        {
            var result = await factory();
            
            if (result != null)
            {
                // Store in L2 cache if using hybrid mode
                if (ShouldUseL2)
                {
                    await _l2Cache.GetOrCreateAsync(
                        methodName,
                        args,
                        () => Task.FromResult(result),
                        settings,
                        keyGenerator,
                        requireIdempotent);
                }
                
                // Store in L1 cache if enabled
                await StoreInL1IfEnabled(cacheKey, result, settings);
            }
            
            return result;
        }
        
        private async Task<(bool Exists, T? Value)> GetL2ValueDirectlyAsync<T>(string cacheKey)
        {
            // Optimized version: reuse cached objects and simplify logic
            try
            {
                var keyGenerator = new SimpleKeyGenerator(cacheKey);
                
                if (_l2Cache == null)
                {
                    return (false, default(T));
                }
                
                var wasFactoryCalled = false;
                var result = await _l2Cache.GetOrCreateAsync<T>(
                    L2DirectGetMethodName,
                    EmptyArgs,
                    () => 
                    {
                        wasFactoryCalled = true;
                        // Return a task that's already completed with default value
                        // This avoids creating new tasks when cache misses
                        return Task.FromResult<T>(default!);
                    },
                    CachedL2Settings,
                    keyGenerator,
                    false);
                
                // If factory was called, there was no cached value
                return wasFactoryCalled ? (false, default(T)) : (true, result);
            }
            catch
            {
                return (false, default(T));
            }
        }

        public async Task InvalidateByTagsAsync(params string[] tags)
        {
            if (tags == null || !tags.Any()) return;
            
            _logger.LogDebug("Invalidating cache by tags: {Tags}", string.Join(", ", tags));
            
            // Efficient L1 invalidation if enabled
            if (_options.EnableEfficientL1TagInvalidation)
            {
                await InvalidateL1ByTagsEfficientlyAsync(tags);
            }
            else
            {
                // Fallback: Clear entire L1 cache
                await _l1Cache.ClearAsync();
                ClearAllTagMappings();
                _logger.LogDebug("Cleared entire L1 cache (efficient tag invalidation disabled)");
            }
            
            // Invalidate L2 cache
            if (_options.L2Enabled)
            {
                await _l2Cache.InvalidateByTagsAsync(tags);
            }
            
            // Notify other instances via backplane
            if (_backplane != null && _options.EnableBackplane)
            {
                await _backplane.PublishInvalidationAsync(tags);
                Interlocked.Increment(ref _backplaneMessagesSent);
            }
        }
        
        /// <summary>
        /// Efficiently invalidates L1 cache entries by specific tags without clearing everything.
        /// </summary>
        private async Task InvalidateL1ByTagsEfficientlyAsync(string[] tags)
        {
            var keysToInvalidate = GetKeysForTags(tags);
            
            if (!keysToInvalidate.Any())
            {
                _logger.LogTrace("No keys found for tags: {Tags}", string.Join(", ", tags));
                return;
            }
            
            _logger.LogDebug("Invalidating {KeyCount} keys for tags: {Tags}", 
                keysToInvalidate.Count, string.Join(", ", tags));
            
            // Remove keys from L1 cache
            var removedCount = await _l1Cache.RemoveMultipleAsync(keysToInvalidate.ToArray());
            
            // Clean up tag mappings for invalidated keys
            foreach (var key in keysToInvalidate)
            {
                UntrackKeyTags(key);
            }
            
            _logger.LogTrace("Successfully invalidated {RemovedCount}/{TotalCount} keys", 
                removedCount, keysToInvalidate.Count);
        }

        public async Task<T?> GetFromL1Async<T>(string key)
        {
            return await _l1Cache.GetAsync<T>(key);
        }

        public async Task<T?> GetFromL2Async<T>(string key)
        {
            if (!_options.L2Enabled) return default;
            
            try
            {
                await _l2Semaphore.WaitAsync();
                var (exists, value) = await GetL2ValueDirectlyAsync<T>(key);
                if (!exists)
                {
                    _logger.LogDebug("L2 cache miss for key {Key}", key);
                }
                return exists ? value : default;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error retrieving key {Key} from L2 cache", key);
                return default;
            }
            finally
            {
                _l2Semaphore.Release();
            }
        }

        public async Task SetInL1Async<T>(string key, T value, TimeSpan expiration)
        {
            if (_options.Strategy == HybridStrategy.L2Only) return;
            
            var effectiveExpiration = expiration > _options.L1MaxExpiration 
                ? _options.L1MaxExpiration 
                : expiration;
                
            await _l1Cache.SetAsync(key, value, effectiveExpiration);
        }
        
        public async Task SetInL1Async<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags)
        {
            if (_options.Strategy == HybridStrategy.L2Only) return;
            
            var effectiveExpiration = expiration > _options.L1MaxExpiration 
                ? _options.L1MaxExpiration 
                : expiration;
                
            await _l1Cache.SetAsync(key, value, effectiveExpiration);
            
            // Track tags for efficient invalidation
            TrackKeyTags(key, tags);
        }

        public async Task SetInL2Async<T>(string key, T value, TimeSpan expiration)
        {
            if (!_options.L2Enabled || _options.Strategy == HybridStrategy.L1Only) return;
            
            try
            {
                await _l2Semaphore.WaitAsync();
                
                // Use GetOrCreateAsync with a factory that returns the value we want to set
                var settings = new CacheMethodSettings { Duration = expiration };
                var keyGenerator = new SimpleKeyGenerator(key);
                
                await _l2Cache.GetOrCreateAsync<T>(
                    "HybridL2Set",
                    Array.Empty<object>(),
                    () => Task.FromResult(value),
                    settings,
                    keyGenerator,
                    false);
                    
                _logger.LogTrace("Set value in L2 cache for key {Key}", key);
            }
            finally
            {
                _l2Semaphore.Release();
            }
        }

        public async Task SetInBothAsync<T>(string key, T value, TimeSpan l1Expiration, TimeSpan l2Expiration)
        {
            await SetInL1Async(key, value, l1Expiration);
            await SetInL2Async(key, value, l2Expiration);
        }

        public async Task InvalidateL1Async(string key)
        {
            await _l1Cache.RemoveAsync(key);
            
            // Clean up tag mappings
            UntrackKeyTags(key);
        }

        public async Task InvalidateL2Async(string key)
        {
            if (!_options.L2Enabled) return;
            
            // WORKAROUND: The ICacheManager interface only provides InvalidateByTagsAsync() but no direct
            // RemoveAsync(key) method for individual key invalidation. As a workaround, we create a 
            // synthetic tag for each key (format: "key:{actualKey}") and use tag-based invalidation.
            // 
            // This approach has significant limitations and trade-offs:
            // 1. DEPENDENCY: Relies on L2 cache implementations supporting tag-based invalidation
            //    - If the L2 provider doesn't support tags, this silently fails
            //    - Some providers may have different tag invalidation semantics
            // 2. PERFORMANCE: Creates additional metadata overhead for tag tracking
            //    - Each cached item gets an extra synthetic tag
            //    - Tag indexes consume additional memory and processing time
            // 3. EFFICIENCY: May be less efficient than direct key removal
            //    - Tag-based invalidation often scans tag indexes rather than direct hash lookups
            //    - Some implementations may clear multiple items when only one is needed
            // 4. CONSISTENCY: Different L2 providers may handle tag invalidation differently
            //    - Redis: Uses SET operations and key scanning
            //    - SQL Server: May use table joins and WHERE clauses
            //    - Memory caches: May use dictionary lookups
            // 5. ERROR HANDLING: Failed invalidations are not easily detectable
            //    - InvalidateByTagsAsync typically returns void, hiding failures
            //    - Stale data may persist in L2 cache without indication
            // 
            // RECOMMENDED SOLUTIONS:
            // - Extend ICacheManager interface to include RemoveAsync(string key) method
            // - Add TryRemoveAsync(string key) : Task<bool> for failure detection
            // - Consider using cache provider-specific interfaces when available
            // 
            // IMPACT: This workaround affects cache consistency guarantees and performance
            // characteristics, making it unsuitable for scenarios requiring strict consistency.
            var invalidationTag = $"key:{key}";
            await _l2Cache.InvalidateByTagsAsync(invalidationTag);
            _logger.LogTrace("Invalidated L2 cache for key {Key} using synthetic tag {Tag}", key, invalidationTag);
        }

        public async Task InvalidateBothAsync(string key)
        {
            await InvalidateL1Async(key);
            await InvalidateL2Async(key);
            
            // Notify other instances
            if (_backplane != null && _options.EnableBackplane)
            {
                await _backplane.PublishKeyInvalidationAsync(key);
                Interlocked.Increment(ref _backplaneMessagesSent);
            }
        }

        public async Task WarmL1CacheAsync(params string[] keys)
        {
            if (!_options.EnableL1Warming || !_options.L2Enabled) return;
            
            foreach (var key in keys)
            {
                await WarmL1CacheKeyAsync(key);
            }
        }
        
        public async Task WarmL1CacheKeyAsync<T>(string key)
        {
            if (!_options.EnableL1Warming || !_options.L2Enabled) return;
            
            var l2Value = await GetFromL2Async<T>(key);
            if (l2Value != null)
            {
                await SetInL1Async(key, l2Value, _options.L1DefaultExpiration);
                // Note: Tags are not preserved during warming since we don't have access to original settings
                // This is a limitation of the current design
            }
        }
        
        public async Task WarmL1CacheKeyAsync<T>(string key, IEnumerable<string> tags)
        {
            if (!_options.EnableL1Warming || !_options.L2Enabled) return;
            
            var l2Value = await GetFromL2Async<T>(key);
            if (l2Value != null)
            {
                await SetInL1Async(key, l2Value, _options.L1DefaultExpiration, tags);
            }
        }
        
        private async Task WarmL1CacheKeyAsync(string key)
        {
            // Generic fallback for when type is unknown
            var l2Value = await GetFromL2Async<object>(key);
            if (l2Value != null)
            {
                await SetInL1Async(key, l2Value, _options.L1DefaultExpiration);
                // Note: Tags are not preserved during warming since we don't have access to original settings
            }
        }

        public async Task<HybridCacheStats> GetStatsAsync()
        {
            var l1Stats = await _l1Cache.GetStatsAsync();
            
            int tagMappingCount = 0;
            int uniqueTagCount = 0;
            
            if (_options.EnableEfficientL1TagInvalidation)
            {
                _tagMappingLock.EnterReadLock();
                try
                {
                    tagMappingCount = _tagMappingCount;
                    uniqueTagCount = _tagToKeys.Count;
                }
                finally
                {
                    _tagMappingLock.ExitReadLock();
                }
            }
            
            return new HybridCacheStats
            {
                L1Hits = _l1Hits,
                L1Misses = _l1Misses,
                L2Hits = _l2Hits,
                L2Misses = _l2Misses,
                L1Entries = l1Stats.Entries,
                L1Evictions = l1Stats.Evictions,
                BackplaneMessagesSent = _backplaneMessagesSent,
                BackplaneMessagesReceived = _backplaneMessagesReceived,
                TagMappingCount = tagMappingCount,
                UniqueTagCount = uniqueTagCount,
                EfficientTagInvalidationEnabled = _options.EnableEfficientL1TagInvalidation
            };
        }

        public async Task EvictFromL1Async(string key)
        {
            await _l1Cache.RemoveAsync(key);
            
            // Clean up tag mappings
            UntrackKeyTags(key);
        }

        public async Task SyncL1CacheAsync()
        {
            if (_backplane == null || !_options.EnableBackplane)
            {
                _logger.LogWarning("L1 cache sync requires a configured backplane");
                return;
            }

            try
            {
                _logger.LogDebug("Triggering L1 cache synchronization across instances");
                
                // Use a special sync tag to trigger cache clearing on other instances
                // This ensures all L1 caches are cleared and will be refreshed from L2
                var syncTag = $"l1sync:{Guid.NewGuid()}";
                await _backplane.PublishInvalidationAsync(syncTag);
                
                // Clear our own L1 cache to ensure consistency
                await _l1Cache.ClearAsync();
                ClearAllTagMappings();
                
                _logger.LogInformation("L1 cache synchronization completed");
                Interlocked.Increment(ref _backplaneMessagesSent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during L1 cache synchronization");
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            
            try
            {
                if (_backplane != null && _options.EnableBackplane)
                {
                    _backplane.InvalidationReceived -= OnBackplaneInvalidationReceived;
                    // Properly await the async disposal
                    await _backplane.StopListeningAsync();
                    _backplane.Dispose();
                }
                
                // Clean up key-level locks
                foreach (var kvp in _keyLevelLocks)
                {
                    kvp.Value.Dispose();
                }
                _keyLevelLocks.Clear();
                
                // Clean up tag mappings
                ClearAllTagMappings();
                _tagMappingLock?.Dispose();
                
                if (_l1Cache is IAsyncDisposable asyncL1Cache)
                {
                    await asyncL1Cache.DisposeAsync();
                }
                else
                {
                    _l1Cache?.Dispose();
                }
                
                _l2Semaphore?.Dispose();
            }
            finally
            {
                _disposed = true;
            }
        }
        
        public void Dispose()
        {
            // For synchronous disposal, we have to block (unfortunately)
            // But we warn about it
            if (_backplane != null && _options.EnableBackplane)
            {
                // Consider logging a warning here about blocking disposal
            }
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private TimeSpan CalculateL1Expiration(CacheMethodSettings settings)
        {
            var requestedExpiration = settings.Duration ?? _options.L1DefaultExpiration;
            return requestedExpiration > _options.L1MaxExpiration 
                ? _options.L1MaxExpiration 
                : requestedExpiration;
        }

        private void OnBackplaneInvalidationReceived(object? sender, CacheInvalidationEventArgs e)
        {
            // Skip if this is our own message
            if (e.SourceInstanceId == _options.InstanceId) return;
            
            Interlocked.Increment(ref _backplaneMessagesReceived);
            
            _logger.LogDebug("Received backplane invalidation from {SourceInstance} for type {Type}", 
                e.SourceInstanceId, e.Type);
            
            // Handle async operations in a fire-and-forget manner with proper error handling
            _ = Task.Run(async () =>
            {
                try
                {
                    switch (e.Type)
                    {
                        case InvalidationType.ByTags:
                            // Use efficient tag-based invalidation if enabled
                            if (_options.EnableEfficientL1TagInvalidation && e.Tags != null)
                            {
                                await InvalidateL1ByTagsEfficientlyAsync(e.Tags);
                            }
                            else
                            {
                                await _l1Cache.ClearAsync();
                                ClearAllTagMappings();
                            }
                            break;
                            
                        case InvalidationType.ByKeys:
                            // Remove specific keys from L1
                            if (e.Keys != null && e.Keys.Any())
                            {
                                await _l1Cache.RemoveMultipleAsync(e.Keys);
                                // Clean up tag mappings for removed keys
                                foreach (var key in e.Keys)
                                {
                                    UntrackKeyTags(key);
                                }
                            }
                            break;
                            
                        case InvalidationType.ClearAll:
                            // Clear entire L1 cache
                            await _l1Cache.ClearAsync();
                            ClearAllTagMappings();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing backplane invalidation");
                }
            });
        }

        #region Tag Tracking Infrastructure
        
        /// <summary>
        /// Associates a cache key with its tags for efficient invalidation.
        /// </summary>
        private void TrackKeyTags(string key, IEnumerable<string> tags)
        {
            if (!_options.EnableEfficientL1TagInvalidation || tags == null) return;
            
            var tagList = tags.ToList();
            if (!tagList.Any()) return;
            
            _tagMappingLock.EnterWriteLock();
            try
            {
                // Check if we're exceeding the mapping limit
                if (_tagMappingCount >= _options.MaxTagMappings)
                {
                    // Clean up some old mappings (simple FIFO approach)
                    CleanupOldTagMappings();
                }
                
                // Track key -> tags mapping
                var keyTags = _keyToTags.GetOrAdd(key, _ => new ConcurrentDictionary<string, byte>());
                
                foreach (var tag in tagList)
                {
                    // Track tag -> keys mapping
                    var tagKeys = _tagToKeys.GetOrAdd(tag, _ => new ConcurrentDictionary<string, byte>());
                    
                    // Add bidirectional mapping
                    if (tagKeys.TryAdd(key, 0) && keyTags.TryAdd(tag, 0))
                    {
                        Interlocked.Increment(ref _tagMappingCount);
                    }
                }
            }
            finally
            {
                _tagMappingLock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// Removes tag associations for a cache key.
        /// </summary>
        private void UntrackKeyTags(string key)
        {
            if (!_options.EnableEfficientL1TagInvalidation) return;
            
            _tagMappingLock.EnterWriteLock();
            try
            {
                if (_keyToTags.TryRemove(key, out var keyTags))
                {
                    foreach (var tag in keyTags.Keys)
                    {
                        if (_tagToKeys.TryGetValue(tag, out var tagKeys) && tagKeys.TryRemove(key, out _))
                        {
                            Interlocked.Decrement(ref _tagMappingCount);
                            
                            // Clean up empty tag mappings
                            if (tagKeys.IsEmpty)
                            {
                                _tagToKeys.TryRemove(tag, out _);
                            }
                        }
                    }
                }
            }
            finally
            {
                _tagMappingLock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// Gets all keys associated with given tags.
        /// </summary>
        private HashSet<string> GetKeysForTags(string[] tags)
        {
            if (!_options.EnableEfficientL1TagInvalidation || tags == null || !tags.Any())
                return new HashSet<string>();
                
            var keys = new HashSet<string>();
            
            _tagMappingLock.EnterReadLock();
            try
            {
                foreach (var tag in tags)
                {
                    if (_tagToKeys.TryGetValue(tag, out var tagKeys))
                    {
                        foreach (var key in tagKeys.Keys)
                        {
                            keys.Add(key);
                        }
                    }
                }
            }
            finally
            {
                _tagMappingLock.ExitReadLock();
            }
            
            return keys;
        }
        
        /// <summary>
        /// Cleans up old tag mappings when limit is exceeded.
        /// </summary>
        private void CleanupOldTagMappings()
        {
            // Simple cleanup: remove 10% of mappings
            var targetCleanup = Math.Max(1, _options.MaxTagMappings / 10);
            var cleaned = 0;
            
            foreach (var kvp in _keyToTags.ToArray())
            {
                if (cleaned >= targetCleanup) break;
                
                UntrackKeyTags(kvp.Key);
                cleaned++;
            }
            
            _logger.LogDebug("Cleaned up {CleanedCount} tag mappings, current count: {CurrentCount}", 
                cleaned, _tagMappingCount);
        }
        
        /// <summary>
        /// Clears all tag mappings.
        /// </summary>
        private void ClearAllTagMappings()
        {
            if (!_options.EnableEfficientL1TagInvalidation) return;
            
            _tagMappingLock.EnterWriteLock();
            try
            {
                _tagToKeys.Clear();
                _keyToTags.Clear();
                _tagMappingCount = 0;
            }
            finally
            {
                _tagMappingLock.ExitWriteLock();
            }
        }
        
        #endregion
        
        // Simple key generator for internal use
        private class SimpleKeyGenerator : ICacheKeyGenerator
        {
            private readonly string _key;
            
            public SimpleKeyGenerator(string key)
            {
                _key = key;
            }
            
            public string GenerateKey(string methodName, object[] args, CacheMethodSettings settings)
            {
                return _key;
            }
        }
    }
}