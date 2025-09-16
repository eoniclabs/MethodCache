using MethodCache.Core.Configuration;
using MethodCache.ETags.Abstractions;
using MethodCache.ETags.Models;
using MethodCache.ETags.Utilities;
using MethodCache.HybridCache.Abstractions;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace MethodCache.ETags.Implementation
{
    /// <summary>
    /// ETag-aware cache manager that leverages hybrid L1/L2 caching for optimal performance.
    /// Provides HTTP ETag semantics with distributed cache consistency.
    /// </summary>
    public class ETagHybridCacheManager : IETagCacheManager, IDisposable
    {
        private readonly IHybridCacheManager _hybridCache;
        private readonly IETagCacheBackplane? _backplane;
        private readonly ILogger<ETagHybridCacheManager> _logger;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();
        private bool _disposed;

        public ETagHybridCacheManager(
            IHybridCacheManager hybridCache,
            ILogger<ETagHybridCacheManager> logger,
            IETagCacheBackplane? backplane = null)
        {
            _hybridCache = hybridCache ?? throw new ArgumentNullException(nameof(hybridCache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _backplane = backplane;

            // Subscribe to backplane events if available
            if (_backplane != null)
            {
                _backplane.ETagInvalidationReceived += OnETagInvalidationReceived;
                _logger.LogDebug("ETag backplane wired up successfully");
            }
        }

        public async Task<ETagCacheResult<T>> GetOrCreateWithETagAsync<T>(
            string key,
            Func<Task<ETagCacheEntry<T>>> factory,
            string? ifNoneMatch = null,
            CacheMethodSettings? settings = null,
            bool forceRefresh = false)
        {
            return await GetOrCreateWithETagAsync<T>(
                key,
                async _ => await factory(),
                ifNoneMatch,
                settings,
                forceRefresh);
        }

        public async Task<ETagCacheResult<T>> GetOrCreateWithETagAsync<T>(
            string key,
            Func<string?, Task<ETagCacheEntry<T>>> factory,
            string? ifNoneMatch = null,
            CacheMethodSettings? settings = null,
            bool forceRefresh = false)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Cache key cannot be null or empty", nameof(key));

            var etagKey = GetETagKey(key);
            settings ??= new CacheMethodSettings();

            if (!forceRefresh)
            {
                // 1. Atomically check L1 cache for both ETag and value
                var l1ETagTask = _hybridCache.GetFromL1Async<string>(etagKey);
                var l1ValueTask = _hybridCache.GetFromL1Async<T>(key);
                await Task.WhenAll(l1ETagTask, l1ValueTask);

                var l1ETag = l1ETagTask.Result;
                var l1Value = l1ValueTask.Result;

                // If we have both ETag and value in L1, they're guaranteed to be consistent
                if (l1ETag != null && l1Value != null)
                {
                    if (l1ETag == ifNoneMatch)
                    {
                        _logger.LogDebug("ETag match in L1 cache for key {Key}: {ETag}", key, l1ETag);
                        return ETagCacheResult<T>.NotModified(l1ETag);
                    }
                    
                    _logger.LogDebug("Cache hit in L1 for key {Key} with ETag {ETag}", key, l1ETag);
                    return ETagCacheResult<T>.Hit(l1Value, l1ETag);
                }
            }

            string? currentETag = null;
            if (!forceRefresh)
            {
                // 2. Atomically check L2 cache for both ETag and value
                var l2ETagTask = _hybridCache.GetFromL2Async<string>(etagKey);
                var l2ValueTask = _hybridCache.GetFromL2Async<T>(key);
                await Task.WhenAll(l2ETagTask, l2ValueTask);

                var l2ETag = l2ETagTask.Result;
                var l2Value = l2ValueTask.Result;
                currentETag = l2ETag;

                // If we have both ETag and value in L2, they're guaranteed to be consistent
                if (l2ETag != null && l2Value != null)
                {
                    if (l2ETag == ifNoneMatch)
                    {
                        _logger.LogDebug("ETag match in L2 cache for key {Key}: {ETag}", key, l2ETag);
                        return ETagCacheResult<T>.NotModified(l2ETag);
                    }

                    // Atomically warm L1 with both value and ETag, preserving tags
                    var l1Expiration = CalculateL1Expiration(settings);
                    if (settings.Tags?.Any() == true)
                    {
                        await Task.WhenAll(
                            _hybridCache.SetInL1Async(key, l2Value, l1Expiration, settings.Tags),
                            _hybridCache.SetInL1Async(etagKey, l2ETag, l1Expiration, settings.Tags)
                        );
                    }
                    else
                    {
                        await Task.WhenAll(
                            _hybridCache.SetInL1Async(key, l2Value, l1Expiration),
                            _hybridCache.SetInL1Async(etagKey, l2ETag, l1Expiration)
                        );
                    }

                    _logger.LogDebug("Cache hit in L2 for key {Key} with ETag {ETag}, warmed L1", key, l2ETag);
                    return ETagCacheResult<T>.Hit(l2Value, l2ETag);
                }
            }

            // 3. Cache miss or forced refresh - execute factory function with stampede prevention
            var logMessage = forceRefresh ? "Force refresh for key {Key}, executing factory" : "Cache miss for key {Key}, executing factory";
            _logger.LogDebug(logMessage, key);
            
            // Use per-key semaphore to prevent stampeding herd
            var keyLock = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            
            await keyLock.WaitAsync();
            try
            {
                // Double-check cache after acquiring lock (another thread might have populated it)
                if (!forceRefresh)
                {
                    var reCheckETag = await _hybridCache.GetFromL1Async<string>(etagKey);
                    var reCheckValue = await _hybridCache.GetFromL1Async<T>(key);
                    
                    if (reCheckETag != null && reCheckValue != null)
                    {
                        if (reCheckETag == ifNoneMatch)
                        {
                            _logger.LogDebug("ETag match after lock acquisition for key {Key}: {ETag}", key, reCheckETag);
                            return ETagCacheResult<T>.NotModified(reCheckETag);
                        }
                        
                        _logger.LogDebug("Cache populated by another thread for key {Key} with ETag {ETag}", key, reCheckETag);
                        return ETagCacheResult<T>.Hit(reCheckValue, reCheckETag);
                    }
                }
                
                var newEntry = await factory(currentETag);

                // Handle "not modified" response from factory
                if (newEntry.IsNotModified)
                {
                    _logger.LogDebug("Factory returned not modified for key {Key}: {ETag}", key, newEntry.ETag);
                    return ETagCacheResult<T>.NotModified(newEntry.ETag);
                }

                // Handle bypass response
                if (newEntry.IsBypass)
                {
                    _logger.LogDebug("Factory returned bypass for key {Key}", key);
                    return ETagCacheResult<T>.Bypass();
                }

                // 4. Atomically store both value and ETag in both cache layers
                await StoreInBothLayers(key, newEntry, settings);

                _logger.LogDebug("Cached new value for key {Key} with ETag {ETag}", key, newEntry.ETag);
                return ETagCacheResult<T>.Miss(newEntry.Value!, newEntry.ETag, newEntry.LastModified, newEntry.Metadata);
            }
            finally
            {
                keyLock.Release();
                
                // Clean up unused semaphores periodically to prevent memory leaks
                if (_keyLocks.Count > 1000)
                {
                    _ = Task.Run(() => CleanupUnusedLocks());
                }
            }
        }

        public async Task InvalidateETagAsync(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Cache key cannot be null or empty", nameof(key));

            var etagKey = GetETagKey(key);

            // Invalidate both value and ETag from both layers
            await Task.WhenAll(
                _hybridCache.InvalidateL1Async(key),
                _hybridCache.InvalidateL1Async(etagKey),
                _hybridCache.InvalidateL2Async(key),
                _hybridCache.InvalidateL2Async(etagKey)
            );

            // Publish invalidation to backplane
            if (_backplane != null)
            {
                try
                {
                    await _backplane.PublishETagInvalidationAsync(key);
                    _logger.LogDebug("Published ETag invalidation to backplane for key {Key}", key);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to publish ETag invalidation to backplane for key {Key}", key);
                }
            }

            _logger.LogDebug("Invalidated ETag cache for key {Key}", key);
        }

        public async Task InvalidateETagsAsync(params string[] keys)
        {
            if (keys == null || keys.Length == 0) return;

            // Batch local invalidations
            var etagKeys = keys.Select(GetETagKey).ToArray();
            var allKeys = keys.Concat(etagKeys).ToArray();

            await Task.WhenAll(
                Task.WhenAll(allKeys.Select(_hybridCache.InvalidateL1Async)),
                Task.WhenAll(allKeys.Select(_hybridCache.InvalidateL2Async))
            );

            // Batch publish to backplane
            if (_backplane != null)
            {
                try
                {
                    var invalidations = keys.Select(key => new KeyValuePair<string, string?>(key, null));
                    await _backplane.PublishETagInvalidationBatchAsync(invalidations);
                    _logger.LogDebug("Published batch ETag invalidation to backplane for {Count} keys", keys.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to publish batch ETag invalidation to backplane");
                }
            }

            _logger.LogDebug("Invalidated ETag cache for {Count} keys", keys.Length);
        }

        public async Task<string?> GetETagAsync(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Cache key cannot be null or empty", nameof(key));

            var etagKey = GetETagKey(key);

            // Try L1 first, then L2
            var etag = await _hybridCache.GetFromL1Async<string>(etagKey);
            if (etag != null)
            {
                return etag;
            }

            etag = await _hybridCache.GetFromL2Async<string>(etagKey);
            if (etag != null)
            {
                // Warm L1 cache with shorter expiration for ETags
                var l1Settings = new CacheMethodSettings { Duration = TimeSpan.FromMinutes(5) };
                await _hybridCache.SetInL1Async(etagKey, etag, CalculateL1Expiration(l1Settings));
            }

            return etag;
        }

        public async Task<bool> IsETagValidAsync(string key, string etag)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Cache key cannot be null or empty", nameof(key));
            
            if (string.IsNullOrEmpty(etag))
                return false;

            var currentETag = await GetETagAsync(key);
            return ETagUtilities.ETagsMatch(currentETag, etag);
        }

        private static string GetETagKey(string key) => $"{key}:etag";

        private static TimeSpan CalculateL1Expiration(CacheMethodSettings settings)
        {
            // Use shorter L1 expiration for ETags to ensure freshness
            var baseExpiration = settings.Duration ?? TimeSpan.FromMinutes(30);
            return baseExpiration > TimeSpan.FromHours(1) 
                ? TimeSpan.FromMinutes(30) 
                : baseExpiration;
        }

        private async Task StoreInBothLayers<T>(string key, ETagCacheEntry<T> entry, CacheMethodSettings settings)
        {
            var etagKey = GetETagKey(key);
            var l1Expiration = CalculateL1Expiration(settings);
            var l2Expiration = settings.Duration ?? TimeSpan.FromHours(24);

            // Store in both L1 and L2 caches
            var tasks = new List<Task>();
            
            // L1 cache - apply tags to both value and ETag keys
            if (settings.Tags?.Any() == true)
            {
                tasks.Add(_hybridCache.SetInL1Async(key, entry.Value, l1Expiration, settings.Tags));
                tasks.Add(_hybridCache.SetInL1Async(etagKey, entry.ETag, l1Expiration, settings.Tags));
            }
            else
            {
                tasks.Add(_hybridCache.SetInL1Async(key, entry.Value, l1Expiration));
                tasks.Add(_hybridCache.SetInL1Async(etagKey, entry.ETag, l1Expiration));
            }

            // L2 cache - L2 doesn't support tags in current implementation
            tasks.Add(_hybridCache.SetInL2Async(key, entry.Value, l2Expiration));
            tasks.Add(_hybridCache.SetInL2Async(etagKey, entry.ETag, l2Expiration));

            await Task.WhenAll(tasks);
        }

        private async void OnETagInvalidationReceived(object? sender, ETagInvalidationEventArgs e)
        {
            try
            {
                _logger.LogDebug("Received ETag invalidation from backplane for key {Key}", e.Key);
                
                var etagKey = GetETagKey(e.Key);
                
                // Only invalidate local caches, don't republish to backplane
                await Task.WhenAll(
                    _hybridCache.InvalidateL1Async(e.Key),
                    _hybridCache.InvalidateL1Async(etagKey)
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling ETag invalidation from backplane for key {Key}", e.Key);
            }
        }

        private void CleanupUnusedLocks()
        {
            try
            {
                var locksToRemove = new List<string>();
                
                foreach (var kvp in _keyLocks)
                {
                    if (kvp.Value.CurrentCount == 1) // Not in use
                    {
                        locksToRemove.Add(kvp.Key);
                    }
                }
                
                // Remove up to half of unused locks
                var removeCount = Math.Min(locksToRemove.Count, _keyLocks.Count / 2);
                for (int i = 0; i < removeCount; i++)
                {
                    if (_keyLocks.TryRemove(locksToRemove[i], out var semaphore))
                    {
                        semaphore.Dispose();
                    }
                }
                
                _logger.LogDebug("Cleaned up {Count} unused key locks", removeCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during key lock cleanup");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            if (_backplane != null)
            {
                _backplane.ETagInvalidationReceived -= OnETagInvalidationReceived;
            }

            // Dispose all key locks
            foreach (var kvp in _keyLocks)
            {
                kvp.Value.Dispose();
            }
            _keyLocks.Clear();

            _semaphore.Dispose();
            _disposed = true;
        }
    }
}