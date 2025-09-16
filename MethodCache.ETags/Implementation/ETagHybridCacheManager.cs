using MethodCache.Core.Configuration;
using MethodCache.ETags.Abstractions;
using MethodCache.ETags.Models;
using MethodCache.ETags.Utilities;
using MethodCache.HybridCache.Abstractions;
using Microsoft.Extensions.Logging;

namespace MethodCache.ETags.Implementation
{
    /// <summary>
    /// ETag-aware cache manager that leverages hybrid L1/L2 caching for optimal performance.
    /// Provides HTTP ETag semantics with distributed cache consistency.
    /// </summary>
    public class ETagHybridCacheManager : IETagCacheManager
    {
        private readonly IHybridCacheManager _hybridCache;
        private readonly ILogger<ETagHybridCacheManager> _logger;

        public ETagHybridCacheManager(
            IHybridCacheManager hybridCache,
            ILogger<ETagHybridCacheManager> logger)
        {
            _hybridCache = hybridCache ?? throw new ArgumentNullException(nameof(hybridCache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<ETagCacheResult<T>> GetOrCreateWithETagAsync<T>(
            string key,
            Func<Task<ETagCacheEntry<T>>> factory,
            string? ifNoneMatch = null,
            CacheMethodSettings? settings = null)
        {
            return await GetOrCreateWithETagAsync<T>(
                key,
                async _ => await factory(),
                ifNoneMatch,
                settings);
        }

        public async Task<ETagCacheResult<T>> GetOrCreateWithETagAsync<T>(
            string key,
            Func<string?, Task<ETagCacheEntry<T>>> factory,
            string? ifNoneMatch = null,
            CacheMethodSettings? settings = null)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Cache key cannot be null or empty", nameof(key));

            var etagKey = GetETagKey(key);
            settings ??= new CacheMethodSettings();

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

            // 2. Atomically check L2 cache for both ETag and value
            var l2ETagTask = _hybridCache.GetFromL2Async<string>(etagKey);
            var l2ValueTask = _hybridCache.GetFromL2Async<T>(key);
            await Task.WhenAll(l2ETagTask, l2ValueTask);

            var l2ETag = l2ETagTask.Result;
            var l2Value = l2ValueTask.Result;

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

            // 3. Cache miss - execute factory function
            _logger.LogDebug("Cache miss for key {Key}, executing factory", key);
            
            // Pass the most recent ETag (prefer L2 over L1 as it's more likely to be fresh)
            var currentETag = l2ETag ?? l1ETag;
            var newEntry = await factory(currentETag);

            // Handle "not modified" response from factory
            if (newEntry.IsNotModified)
            {
                _logger.LogDebug("Factory returned not modified for key {Key}: {ETag}", key, newEntry.ETag);
                return ETagCacheResult<T>.NotModified(newEntry.ETag);
            }

            // 4. Atomically store both value and ETag in both cache layers
            await StoreInBothLayers(key, newEntry, settings);

            _logger.LogDebug("Cached new value for key {Key} with ETag {ETag}", key, newEntry.ETag);
            return ETagCacheResult<T>.Miss(newEntry.Value!, newEntry.ETag, newEntry.LastModified, newEntry.Metadata);
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

            _logger.LogDebug("Invalidated ETag cache for key {Key}", key);
        }

        public async Task InvalidateETagsAsync(params string[] keys)
        {
            if (keys == null || keys.Length == 0) return;

            var tasks = keys.Select(InvalidateETagAsync);
            await Task.WhenAll(tasks);
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
    }
}