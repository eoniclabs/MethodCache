using MethodCache.Core.Configuration;
using MethodCache.ETags.Abstractions;
using MethodCache.ETags.Models;
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

            // 1. Fast path: Check L1 cache for ETag first
            var cachedETag = await _hybridCache.GetFromL1Async<string>(etagKey);
            if (cachedETag != null && cachedETag == ifNoneMatch)
            {
                _logger.LogDebug("ETag match in L1 cache for key {Key}: {ETag}", key, cachedETag);
                return ETagCacheResult<T>.NotModified(cachedETag);
            }

            // 2. Check L1 for the actual value if we have an ETag
            if (cachedETag != null)
            {
                var l1Value = await _hybridCache.GetFromL1Async<T>(key);
                if (l1Value != null)
                {
                    _logger.LogDebug("Cache hit in L1 for key {Key} with ETag {ETag}", key, cachedETag);
                    return ETagCacheResult<T>.Hit(l1Value, cachedETag);
                }
            }

            // 3. Check L2 cache for ETag
            var l2ETag = await _hybridCache.GetFromL2Async<string>(etagKey);
            if (l2ETag != null && l2ETag == ifNoneMatch)
            {
                _logger.LogDebug("ETag match in L2 cache for key {Key}: {ETag}", key, l2ETag);
                return ETagCacheResult<T>.NotModified(l2ETag);
            }

            // 4. Check L2 for the actual value
            if (l2ETag != null)
            {
                var l2Value = await _hybridCache.GetFromL2Async<T>(key);
                if (l2Value != null)
                {
                    // Warm L1 with both value and ETag
                    var l1Expiration = CalculateL1Expiration(settings);
                    await Task.WhenAll(
                        _hybridCache.SetInL1Async(key, l2Value, l1Expiration),
                        _hybridCache.SetInL1Async(etagKey, l2ETag, l1Expiration)
                    );

                    _logger.LogDebug("Cache hit in L2 for key {Key} with ETag {ETag}, warmed L1", key, l2ETag);
                    return ETagCacheResult<T>.Hit(l2Value, l2ETag);
                }
            }

            // 5. Cache miss - execute factory function
            _logger.LogDebug("Cache miss for key {Key}, executing factory", key);
            
            var currentETag = l2ETag ?? cachedETag; // Pass the most recent ETag to factory
            var newEntry = await factory(currentETag);

            // Handle "not modified" response from factory
            if (newEntry.IsNotModified)
            {
                _logger.LogDebug("Factory returned not modified for key {Key}: {ETag}", key, newEntry.ETag);
                return ETagCacheResult<T>.NotModified(newEntry.ETag);
            }

            // 6. Store both value and ETag in both cache layers
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
                // Warm L1 cache
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
            return currentETag == etag;
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
            
            // L1 cache
            if (settings.Tags?.Any() == true)
            {
                tasks.Add(_hybridCache.SetInL1Async(key, entry.Value, l1Expiration, settings.Tags));
            }
            else
            {
                tasks.Add(_hybridCache.SetInL1Async(key, entry.Value, l1Expiration));
            }
            
            tasks.Add(_hybridCache.SetInL1Async(etagKey, entry.ETag, l1Expiration));

            // L2 cache  
            tasks.Add(_hybridCache.SetInL2Async(key, entry.Value, l2Expiration));
            tasks.Add(_hybridCache.SetInL2Async(etagKey, entry.ETag, l2Expiration));

            await Task.WhenAll(tasks);
        }
    }
}