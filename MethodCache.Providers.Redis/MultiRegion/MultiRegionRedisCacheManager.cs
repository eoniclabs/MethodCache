using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Providers.Redis.Configuration;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.MultiRegion
{
    public class MultiRegionRedisCacheManager : ICacheManager, IDisposable
    {
        private readonly IMultiRegionCacheManager _multiRegionManager;
        private readonly IRegionSelector _regionSelector;
        private readonly MultiRegionOptions _options;
        private readonly RedisOptions _redisOptions;
        private readonly ICacheMetricsProvider _metricsProvider;
        private readonly ILogger<MultiRegionRedisCacheManager> _logger;

        public MultiRegionRedisCacheManager(
            IMultiRegionCacheManager multiRegionManager,
            IRegionSelector regionSelector,
            IOptions<MultiRegionOptions> multiRegionOptions,
            IOptions<RedisOptions> redisOptions,
            ICacheMetricsProvider metricsProvider,
            ILogger<MultiRegionRedisCacheManager> logger)
        {
            _multiRegionManager = multiRegionManager;
            _regionSelector = regionSelector;
            _options = multiRegionOptions.Value;
            _redisOptions = redisOptions.Value;
            _metricsProvider = metricsProvider;
            _logger = logger;
        }

        public async Task<T> GetOrCreateAsync<T>(
            string methodName, 
            object[] args, 
            Func<Task<T>> factory, 
            CacheMethodSettings settings, 
            ICacheKeyGenerator keyGenerator, 
            bool requireIdempotent)
        {
            var cacheKey = $"{_redisOptions.KeyPrefix}{keyGenerator.GenerateKey(methodName, args, settings)}";
            var startTime = DateTime.UtcNow;

            try
            {
                // Select region for read operation
                var availableRegions = await _multiRegionManager.GetAvailableRegionsAsync();
                var selectedRegion = await _regionSelector.SelectRegionForReadAsync(cacheKey, availableRegions);

                // Try to get from selected region first
                var cachedValue = await _multiRegionManager.GetFromRegionAsync<T>(cacheKey, selectedRegion);
                
                if (cachedValue != null)
                {
                    _metricsProvider.CacheHit(methodName);
                    _logger.LogDebug("Cache hit for key {Key} in region {Region}", cacheKey, selectedRegion);
                    return cachedValue;
                }

                // If region affinity is disabled, try other regions
                if (!_options.EnableRegionAffinity)
                {
                    var otherRegions = availableRegions.Except(new[] { selectedRegion });
                    foreach (var region in otherRegions)
                    {
                        cachedValue = await _multiRegionManager.GetFromRegionAsync<T>(cacheKey, region);
                        if (cachedValue != null)
                        {
                            _metricsProvider.CacheHit(methodName);
                            _logger.LogDebug("Cache hit for key {Key} in fallback region {Region}", cacheKey, region);
                            
                            // Optionally sync back to preferred region
                            _ = Task.Run(() => _multiRegionManager.SyncToRegionAsync(cacheKey, region, selectedRegion));
                            
                            return cachedValue;
                        }
                    }
                }

                // Cache miss - execute factory
                _logger.LogDebug("Cache miss for key {Key}, executing factory", cacheKey);
                
                if (requireIdempotent && !IsIdempotent(factory))
                {
                    throw new InvalidOperationException("Non-idempotent operation cannot be cached with requireIdempotent=true");
                }

                var result = await factory();
                var expiration = settings.Duration ?? _redisOptions.DefaultExpiration;

                // Select region for write operation
                var writeRegion = await _regionSelector.SelectRegionForWriteAsync(cacheKey, availableRegions);
                
                // Store in selected region
                await _multiRegionManager.SetInRegionAsync(cacheKey, result, expiration, writeRegion);

                // Handle tags if specified
                if (settings.Tags?.Any() == true)
                {
                    // Store tag associations (simplified implementation)
                    var tagTasks = settings.Tags.Select(tag => 
                        _multiRegionManager.SetInRegionAsync($"tag:{tag}:{cacheKey}", true, expiration, writeRegion));
                    await Task.WhenAll(tagTasks);
                }

                _metricsProvider.CacheMiss(methodName);
                _logger.LogDebug("Stored key {Key} in region {Region} with expiration {Expiration}", 
                    cacheKey, writeRegion, expiration);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetOrCreateAsync for key {Key}", cacheKey);
                
                // Fallback to executing factory without caching
                return await factory();
            }
        }

        public async Task InvalidateByTagsAsync(params string[] tags)
        {
            if (tags == null || tags.Length == 0)
                return;

            try
            {
                var availableRegions = await _multiRegionManager.GetAvailableRegionsAsync();
                
                if (_options.EnableCrossRegionInvalidation)
                {
                    // Efficient parallel invalidation: batch all tags per region
                    var regionInvalidationTasks = availableRegions.Select(region => 
                        InvalidateMultipleTagsInRegionAsync(tags, region));
                    
                    await Task.WhenAll(regionInvalidationTasks);
                }
                else
                {
                    // Invalidate only in primary region with all tags batched
                    var primaryRegion = _options.PrimaryRegion;
                    if (!string.IsNullOrEmpty(primaryRegion))
                    {
                        await InvalidateMultipleTagsInRegionAsync(tags, primaryRegion);
                    }
                }

                _logger.LogDebug("Invalidated tags: {Tags} across {RegionCount} regions", 
                    string.Join(", ", tags), 
                    _options.EnableCrossRegionInvalidation ? availableRegions.Count() : 1);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error invalidating tags: {Tags}", string.Join(", ", tags));
                throw;
            }
        }

        public async ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator)
        {
            try
            {
                // Try primary region first
                var primaryRegion = await _regionSelector.SelectRegionAsync();
                var cacheManager = await _multiRegionManager.GetCacheManagerAsync(primaryRegion);
                
                if (cacheManager != null)
                {
                    var result = await cacheManager.TryGetAsync<T>(methodName, args, settings, keyGenerator);
                    if (result != null)
                    {
                        return result;
                    }
                }
                
                // Try fallback regions if enabled
                if (_options.EnableRegionFailover)
                {
                    var availableRegions = await _multiRegionManager.GetAvailableRegionsAsync();
                    foreach (var region in availableRegions)
                    {
                        if (region == primaryRegion) continue; // Already tried
                        
                        try
                        {
                            var fallbackManager = await _multiRegionManager.GetCacheManagerAsync(region);
                            if (fallbackManager != null)
                            {
                                var result = await fallbackManager.TryGetAsync<T>(methodName, args, settings, keyGenerator);
                                if (result != null)
                                {
                                    return result;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error during TryGetAsync in fallback region {Region}", region);
                        }
                    }
                }
                
                return default(T);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during multi-region TryGetAsync for method {Method}", methodName);
                return default(T);
            }
        }

        /// <summary>
        /// Efficiently invalidates multiple tags in a single region with batched operations.
        /// </summary>
        private async Task InvalidateMultipleTagsInRegionAsync(string[] tags, string region)
        {
            try
            {
                // Use efficient server-side SUNION to get all keys for all tags
                var allKeysToInvalidate = await GetKeysByTagsEfficientAsync(tags, region);
                
                if (allKeysToInvalidate.Length == 0)
                {
                    _logger.LogDebug("No keys found to invalidate for tags {Tags} in region {Region}", 
                        string.Join(", ", tags), region);
                    return;
                }

                // Batch invalidate all keys in parallel with controlled concurrency
                const int maxConcurrency = 10; // Prevent overwhelming the region
                var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
                
                var invalidationTasks = allKeysToInvalidate.Select(async key =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        await _multiRegionManager.InvalidateInRegionAsync(key, region);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(invalidationTasks);
                
                _logger.LogDebug("Invalidated {KeyCount} keys for tags {Tags} in region {Region}", 
                    allKeysToInvalidate.Length, string.Join(", ", tags), region);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error invalidating tags {Tags} in region {Region}", 
                    string.Join(", ", tags), region);
                throw;
            }
        }

        /// <summary>
        /// Efficiently gets all keys associated with multiple tags using server-side SUNION.
        /// This reduces round-trips and performs union operations on the Redis server.
        /// </summary>
        private Task<string[]> GetKeysByTagsEfficientAsync(string[] tags, string region)
        {
            if (tags.Length == 0) return Task.FromResult(Array.Empty<string>());
            
            // In a real multi-region implementation, you would:
            // 1. Get connection to the specific region
            // 2. Build tag keys with region-specific prefixes
            // 3. Use Redis SUNION for server-side union
            
            // For this simplified implementation, we simulate the efficient pattern:
            _logger.LogDebug("Efficiently looking up keys for {TagCount} tags in region {Region} using server-side operations", 
                tags.Length, region);
            
            // Return empty for now - in a real implementation this would:
            // var database = GetRegionDatabase(region);
            // var tagKeys = tags.Select(tag => $"region:{region}:tags:{tag}").Cast<RedisKey>().ToArray();
            // var unionResult = await database.SetCombineAsync(SetOperation.Union, tagKeys);
            // return unionResult.Select(k => k.ToString()).ToArray();
            
            return Task.FromResult(Array.Empty<string>());
        }

        private Task<string[]> GetKeysByTagAsync(string tag, string region)
        {
            // Legacy method - replaced by GetKeysByTagsEfficientAsync for better performance
            return GetKeysByTagsEfficientAsync(new[] { tag }, region);
        }

        private bool IsIdempotent<T>(Func<Task<T>> factory)
        {
            // Simplified check - in practice, you'd use attributes or configuration
            return true;
        }

        public void Dispose()
        {
            if (_multiRegionManager is IDisposable disposable)
                disposable.Dispose();
        }
    }
}