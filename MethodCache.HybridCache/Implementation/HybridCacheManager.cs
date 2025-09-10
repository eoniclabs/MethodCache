using System;
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
    public class HybridCacheManager : IHybridCacheManager
    {
        private readonly IMemoryCache _l1Cache;
        private readonly ICacheManager _l2Cache;
        private readonly ICacheBackplane? _backplane;
        private readonly HybridCacheOptions _options;
        private readonly ILogger<HybridCacheManager> _logger;
        private readonly SemaphoreSlim _l2Semaphore;
        
        // Statistics
        private long _l1Hits;
        private long _l1Misses;
        private long _l2Hits;
        private long _l2Misses;
        private long _backplaneMessagesSent;
        private long _backplaneMessagesReceived;

        public HybridCacheManager(
            IMemoryCache l1Cache,
            ICacheManager l2Cache,
            ICacheBackplane? backplane,
            IOptions<HybridCacheOptions> options,
            ILogger<HybridCacheManager> logger)
        {
            _l1Cache = l1Cache ?? throw new ArgumentNullException(nameof(l1Cache));
            _l2Cache = l2Cache ?? throw new ArgumentNullException(nameof(l2Cache));
            _backplane = backplane;
            _options = options.Value;
            _logger = logger;
            
            _l2Semaphore = new SemaphoreSlim(_options.MaxConcurrentL2Operations, _options.MaxConcurrentL2Operations);
            
            // Subscribe to backplane invalidation events if available
            if (_backplane != null && _options.EnableBackplane)
            {
                _backplane.InvalidationReceived += OnBackplaneInvalidationReceived;
                _ = Task.Run(async () => await _backplane.StartListeningAsync());
                _logger.LogInformation("Hybrid cache backplane enabled for instance {InstanceId}", _options.InstanceId);
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
            
            // Check L2 cache if enabled
            if (_options.L2Enabled && _options.Strategy != HybridStrategy.L1Only)
            {
                try
                {
                    await _l2Semaphore.WaitAsync();
                    
                    // Use L2 cache's GetOrCreateAsync to handle the factory execution
                    var result = await _l2Cache.GetOrCreateAsync(
                        methodName, 
                        args, 
                        factory, 
                        settings, 
                        keyGenerator, 
                        requireIdempotent);
                    
                    if (result != null)
                    {
                        // Store in L1 cache for future access
                        if (_options.Strategy != HybridStrategy.L2Only)
                        {
                            var l1Expiration = CalculateL1Expiration(settings);
                            await _l1Cache.SetAsync(cacheKey, result, l1Expiration);
                        }
                        
                        Interlocked.Increment(ref _l2Hits);
                        _logger.LogTrace("L2 cache hit for key {Key}, warmed L1 cache", cacheKey);
                    }
                    else
                    {
                        Interlocked.Increment(ref _l2Misses);
                    }
                    
                    return result;
                }
                finally
                {
                    _l2Semaphore.Release();
                }
            }
            
            // L2 not enabled or L1Only mode - execute factory and cache in L1
            if (_options.Strategy == HybridStrategy.L1Only)
            {
                var result = await factory();
                if (result != null)
                {
                    var l1Expiration = CalculateL1Expiration(settings);
                    await _l1Cache.SetAsync(cacheKey, result, l1Expiration);
                }
                return result;
            }
            
            // This shouldn't happen if properly configured
            _logger.LogWarning("Hybrid cache misconfiguration - executing factory without caching");
            return await factory();
        }

        public async Task InvalidateByTagsAsync(params string[] tags)
        {
            if (!tags.Any()) return;
            
            _logger.LogDebug("Invalidating cache by tags: {Tags}", string.Join(", ", tags));
            
            // Clear L1 cache (simpler approach - clear all since L1 doesn't track tags)
            await _l1Cache.ClearAsync();
            
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
                
                // Create a dummy method context for L2 cache
                var settings = new CacheMethodSettings { Duration = _options.L2DefaultExpiration };
                var keyGenerator = new SimpleKeyGenerator(key);
                
                return await _l2Cache.GetOrCreateAsync<T>(
                    "HybridL2Get",
                    Array.Empty<object>(),
                    () => Task.FromResult<T>(default!),
                    settings,
                    keyGenerator,
                    false);
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

        public async Task SetInL2Async<T>(string key, T value, TimeSpan expiration)
        {
            if (!_options.L2Enabled || _options.Strategy == HybridStrategy.L1Only) return;
            
            try
            {
                await _l2Semaphore.WaitAsync();
                
                // For now, we can't directly set in L2 through ICacheManager
                // This would require extending the ICacheManager interface
                _logger.LogWarning("Direct L2 set not supported through ICacheManager interface");
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
        }

        public Task InvalidateL2Async(string key)
        {
            // L2 invalidation would require extending ICacheManager
            _logger.LogWarning("Direct L2 key invalidation not supported through ICacheManager interface");
            return Task.CompletedTask;
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
                var l2Value = await GetFromL2Async<object>(key);
                if (l2Value != null)
                {
                    await SetInL1Async(key, l2Value, _options.L1DefaultExpiration);
                }
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
                L1Entries = l1Stats.Entries,
                L1Evictions = l1Stats.Evictions,
                BackplaneMessagesSent = _backplaneMessagesSent,
                BackplaneMessagesReceived = _backplaneMessagesReceived
            };
        }

        public async Task EvictFromL1Async(string key)
        {
            await _l1Cache.RemoveAsync(key);
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
                
                _logger.LogInformation("L1 cache synchronization completed");
                Interlocked.Increment(ref _backplaneMessagesSent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during L1 cache synchronization");
                throw;
            }
        }

        public void Dispose()
        {
            if (_backplane != null && _options.EnableBackplane)
            {
                _backplane.InvalidationReceived -= OnBackplaneInvalidationReceived;
                _backplane.StopListeningAsync().GetAwaiter().GetResult();
                _backplane.Dispose();
            }
            
            _l1Cache?.Dispose();
            _l2Semaphore?.Dispose();
        }

        private TimeSpan CalculateL1Expiration(CacheMethodSettings settings)
        {
            var requestedExpiration = settings.Duration ?? _options.L1DefaultExpiration;
            return requestedExpiration > _options.L1MaxExpiration 
                ? _options.L1MaxExpiration 
                : requestedExpiration;
        }

        private async void OnBackplaneInvalidationReceived(object? sender, CacheInvalidationEventArgs e)
        {
            // Skip if this is our own message
            if (e.SourceInstanceId == _options.InstanceId) return;
            
            Interlocked.Increment(ref _backplaneMessagesReceived);
            
            _logger.LogDebug("Received backplane invalidation from {SourceInstance} for type {Type}", 
                e.SourceInstanceId, e.Type);
            
            try
            {
                switch (e.Type)
                {
                    case InvalidationType.ByTags:
                        // Clear L1 cache when tags are invalidated
                        await _l1Cache.ClearAsync();
                        break;
                        
                    case InvalidationType.ByKeys:
                        // Remove specific keys from L1
                        await _l1Cache.RemoveMultipleAsync(e.Keys);
                        break;
                        
                    case InvalidationType.ClearAll:
                        // Clear entire L1 cache
                        await _l1Cache.ClearAsync();
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing backplane invalidation");
            }
        }

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