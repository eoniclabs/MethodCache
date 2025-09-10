using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core;
using MethodCache.Core.Configuration;

namespace MethodCache.Providers.Redis.Hybrid
{
    public class HybridCacheManager : IHybridCacheManager, IDisposable
    {
        private readonly IL1Cache _l1Cache;
        private readonly ICacheManager _l2Cache; // Redis cache manager
        private readonly HybridCacheOptions _options;
        private readonly ICacheMetricsProvider _metricsProvider;
        private readonly ILogger<HybridCacheManager> _logger;
        private readonly SemaphoreSlim _l2Semaphore;
        
        // Statistics
        private long _l1Hits;
        private long _l1Misses;
        private long _l2Hits;
        private long _l2Misses;
        private long _l1Evictions;

        public HybridCacheManager(
            IL1Cache l1Cache,
            ICacheManager l2Cache,
            IOptions<HybridCacheOptions> options,
            ICacheMetricsProvider metricsProvider,
            ILogger<HybridCacheManager> logger)
        {
            _l1Cache = l1Cache;
            _l2Cache = l2Cache;
            _options = options.Value;
            _metricsProvider = metricsProvider;
            _logger = logger;
            _l2Semaphore = new SemaphoreSlim(_options.MaxConcurrentL2Operations, _options.MaxConcurrentL2Operations);
        }

        public async Task<T> GetOrCreateAsync<T>(
            string methodName,
            object[] args,
            Func<Task<T>> factory,
            CacheMethodSettings settings,
            ICacheKeyGenerator keyGenerator,
            bool requireIdempotent)
        {
            var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);
            var startTime = DateTime.UtcNow;

            try
            {
                // Step 1: Try L1 cache first
                var l1Result = await GetFromL1Async<T>(cacheKey);
                if (l1Result != null)
                {
                    Interlocked.Increment(ref _l1Hits);
                    _metricsProvider.CacheHit(methodName);
                    _logger.LogTrace("L1 cache hit for key {Key}", cacheKey);
                    return l1Result;
                }

                Interlocked.Increment(ref _l1Misses);

                // Step 2: Try L2 cache if enabled
                if (_options.L2Enabled && _options.Strategy != HybridStrategy.L1Only)
                {
                    var l2Result = await GetFromL2Async<T>(cacheKey);
                    if (l2Result != null)
                    {
                        Interlocked.Increment(ref _l2Hits);
                        _metricsProvider.CacheHit(methodName);
                        _logger.LogTrace("L2 cache hit for key {Key}", cacheKey);

                        // Warm L1 cache with L2 result
                        if (_options.EnableL1Warming)
                        {
                            var l1Expiration = TimeSpan.FromTicks(Math.Min(_options.L1DefaultExpiration.Ticks, settings.Duration?.Ticks ?? _options.L1DefaultExpiration.Ticks));
                            _ = Task.Run(() => SetInL1Async(cacheKey, l2Result, l1Expiration));
                        }

                        return l2Result;
                    }
                    
                    Interlocked.Increment(ref _l2Misses);
                }

                // Step 3: Execute factory function
                _logger.LogTrace("Cache miss for key {Key}, executing factory", cacheKey);
                
                if (requireIdempotent && !IsIdempotent(factory))
                {
                    throw new InvalidOperationException("Non-idempotent operation cannot be cached with requireIdempotent=true");
                }

                var result = await factory();
                
                // Step 4: Store in caches based on strategy
                await StoreResultAsync(cacheKey, result, settings);

                _metricsProvider.CacheMiss(methodName);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in hybrid cache GetOrCreateAsync for key {Key}", cacheKey);
                _metricsProvider.CacheError(methodName, ex.Message);
                
                // Fallback to executing factory without caching
                return await factory();
            }
        }

        public async Task<T?> GetFromL1Async<T>(string key)
        {
            try
            {
                return await _l1Cache.GetAsync<T>(key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error getting key {Key} from L1 cache", key);
                return default;
            }
        }

        public async Task<T?> GetFromL2Async<T>(string key)
        {
            if (!_options.L2Enabled)
                return default;

            try
            {
                await _l2Semaphore.WaitAsync(_options.L2OperationTimeout);
                
                // For this simplified implementation, we assume L2 cache has a direct Get method
                // In practice, you'd need to adapt this based on your L2 cache interface
                return await GetFromL2InternalAsync<T>(key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error getting key {Key} from L2 cache", key);
                return default;
            }
            finally
            {
                _l2Semaphore.Release();
            }
        }

        public async Task SetInL1Async<T>(string key, T value, TimeSpan expiration)
        {
            try
            {
                var clampedExpiration = TimeSpan.FromTicks(Math.Min(expiration.Ticks, _options.L1MaxExpiration.Ticks));
                await _l1Cache.SetAsync(key, value, clampedExpiration);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error setting key {Key} in L1 cache", key);
            }
        }

        public async Task SetInL2Async<T>(string key, T value, TimeSpan expiration)
        {
            if (!_options.L2Enabled)
                return;

            try
            {
                await _l2Semaphore.WaitAsync(_options.L2OperationTimeout);
                await SetInL2InternalAsync(key, value, expiration);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error setting key {Key} in L2 cache", key);
            }
            finally
            {
                _l2Semaphore.Release();
            }
        }

        public async Task InvalidateL1Async(string key)
        {
            try
            {
                await _l1Cache.RemoveAsync(key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error invalidating key {Key} in L1 cache", key);
            }
        }

        public async Task InvalidateL2Async(string key)
        {
            if (!_options.L2Enabled)
                return;

            try
            {
                await _l2Semaphore.WaitAsync(_options.L2OperationTimeout);
                await InvalidateL2InternalAsync(key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error invalidating key {Key} in L2 cache", key);
            }
            finally
            {
                _l2Semaphore.Release();
            }
        }

        public async Task InvalidateBothAsync(string key)
        {
            await Task.WhenAll(
                InvalidateL1Async(key),
                InvalidateL2Async(key)
            );
        }

        public async Task InvalidateByTagsAsync(params string[] tags)
        {
            if (tags == null || tags.Length == 0)
                return;

            try
            {
                // Invalidate L2 first (has tag support)
                if (_options.L2Enabled)
                {
                    await _l2Cache.InvalidateByTagsAsync(tags);
                }

                // For L1, we'd need to implement tag tracking
                // For now, we clear L1 entirely when tags are invalidated
                if (_options.EnableL1Invalidation)
                {
                    await _l1Cache.ClearAsync();
                    _logger.LogDebug("Cleared L1 cache due to tag invalidation: {Tags}", string.Join(", ", tags));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error invalidating tags: {Tags}", string.Join(", ", tags));
                throw;
            }
        }

        public async Task<HybridCacheStats> GetStatsAsync()
        {
            var l1Stats = await _l1Cache.GetStatsAsync();
            
            return new HybridCacheStats
            {
                L1Hits = _l1Hits,
                L1Misses = _l1Misses,
                L2Hits = _l2Hits,
                L2Misses = _l2Misses,
                L1Evictions = l1Stats.Evictions,
                L1Entries = l1Stats.Entries,
                L2Entries = 0 // Would need to get this from L2 cache
            };
        }

        public async Task WarmL1FromL2Async(string key)
        {
            if (!_options.EnableL1Warming || !_options.L2Enabled)
                return;

            try
            {
                var l2Value = await GetFromL2Async<object>(key);
                if (l2Value != null)
                {
                    await SetInL1Async(key, l2Value, _options.L1DefaultExpiration);
                    _logger.LogTrace("Warmed L1 cache for key {Key}", key);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error warming L1 cache for key {Key}", key);
            }
        }

        public async Task EvictFromL1Async(string key)
        {
            try
            {
                var removed = await _l1Cache.RemoveAsync(key);
                if (removed)
                {
                    Interlocked.Increment(ref _l1Evictions);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error evicting key {Key} from L1 cache", key);
            }
        }

        private async Task StoreResultAsync<T>(string key, T result, CacheMethodSettings settings)
        {
            var l1Expiration = settings.Duration ?? _options.L1DefaultExpiration;
            var l2Expiration = settings.Duration ?? _options.L2DefaultExpiration;

            switch (_options.Strategy)
            {
                case HybridStrategy.WriteThrough:
                    await Task.WhenAll(
                        SetInL1Async(key, result, l1Expiration),
                        SetInL2Async(key, result, l2Expiration)
                    );
                    break;

                case HybridStrategy.WriteBack:
                    await SetInL1Async(key, result, l1Expiration);
                    if (_options.EnableAsyncL2Writes)
                    {
                        _ = Task.Run(() => SetInL2Async(key, result, l2Expiration));
                    }
                    else
                    {
                        await SetInL2Async(key, result, l2Expiration);
                    }
                    break;

                case HybridStrategy.L1Only:
                    await SetInL1Async(key, result, l1Expiration);
                    break;

                case HybridStrategy.L2Only:
                    await SetInL2Async(key, result, l2Expiration);
                    break;
            }
        }

        private async Task<T?> GetFromL2InternalAsync<T>(string key)
        {
            try
            {
                // Use a unique method name to avoid routing back through hybrid cache
                var methodName = $"__L2Internal_Get_{key}";
                var args = Array.Empty<object>();
                var settings = new CacheMethodSettings { Duration = TimeSpan.FromHours(1) };
                var tempKeyGenerator = new DefaultCacheKeyGenerator();
                
                // Check if value exists by calling GetOrCreate with a factory that throws
                // This way we only get existing values, never create new ones
                var factoryCalled = false;
                try
                {
                    var result = await _l2Cache.GetOrCreateAsync<T>(
                        methodName,
                        args,
                        () => 
                        {
                            factoryCalled = true;
                            throw new InvalidOperationException("Factory should not be called in L2 internal get");
                        },
                        settings,
                        tempKeyGenerator,
                        false);
                    
                    return factoryCalled ? default : result;
                }
                catch (InvalidOperationException)
                {
                    // Factory was called, meaning no cached value exists
                    return default;
                }
            }
            catch
            {
                return default;
            }
        }

        private async Task SetInL2InternalAsync<T>(string key, T value, TimeSpan expiration)
        {
            try
            {
                // Use a unique method name to avoid routing back through hybrid cache
                var methodName = $"__L2Internal_Set_{key}";
                var args = Array.Empty<object>();
                var hybridTag = $"hybrid_{key}";
                var settings = new CacheMethodSettings 
                { 
                    Duration = expiration,
                    Tags = new List<string> { hybridTag }
                };
                var tempKeyGenerator = new DefaultCacheKeyGenerator();
                
                // Force set the value by always returning our value from the factory
                await _l2Cache.GetOrCreateAsync(methodName, args, () => Task.FromResult(value), settings, tempKeyGenerator, false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set value in L2 cache for key: {Key}", key);
            }
        }

        private async Task InvalidateL2InternalAsync(string key)
        {
            try
            {
                // For invalidation, we use tag-based invalidation
                // Since we can't directly invalidate individual keys in the current interface,
                // we'll create a unique tag for each hybrid cache entry
                var hybridTag = $"hybrid_{key}";
                
                // If L2 cache supports tag invalidation, use that
                await _l2Cache.InvalidateByTagsAsync(hybridTag);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to invalidate key in L2 cache: {Key}", key);
            }
        }

        private bool IsIdempotent<T>(Func<Task<T>> factory)
        {
            // Simplified check - in practice, you'd use attributes or configuration
            return true;
        }

        public void Dispose()
        {
            (_l1Cache as IDisposable)?.Dispose();
            (_l2Cache as IDisposable)?.Dispose();
            _l2Semaphore?.Dispose();
        }
    }
}