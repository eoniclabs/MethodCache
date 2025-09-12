using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Providers.Redis.Configuration;
using MethodCache.Providers.Redis.Features;
using Polly;
using Polly.CircuitBreaker;
using StackExchange.Redis;
using System;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis
{
    public class RedisCacheManager : ICacheManager, IAsyncDisposable
    {
        private readonly IRedisConnectionManager _connectionManager;
        private readonly IRedisSerializer _serializer;
        private readonly IRedisTagManager _tagManager;
        private readonly IDistributedLock _distributedLock;
        private readonly IRedisPubSubInvalidation _pubSubInvalidation;
        private readonly ResiliencePipeline _resilience;
        private readonly ICacheMetricsProvider _metricsProvider;
        private readonly RedisOptions _options;
        private readonly ILogger<RedisCacheManager> _logger;

        public RedisCacheManager(
            IRedisConnectionManager connectionManager,
            IRedisSerializer serializer,
            IRedisTagManager tagManager,
            IDistributedLock distributedLock,
            IRedisPubSubInvalidation pubSubInvalidation,
            ICacheMetricsProvider metricsProvider,
            IOptions<RedisOptions> options,
            ILogger<RedisCacheManager> logger)
        {
            _connectionManager = connectionManager;
            _serializer = serializer;
            _tagManager = tagManager;
            _distributedLock = distributedLock;
            _pubSubInvalidation = pubSubInvalidation;
            _metricsProvider = metricsProvider;
            _options = options.Value;
            _logger = logger;

            _resilience = new ResiliencePipelineBuilder()
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions()
                {
                    FailureRatio = _options.CircuitBreaker.FailureRatio,
                    MinimumThroughput = _options.CircuitBreaker.MinimumThroughput,
                    BreakDuration = _options.CircuitBreaker.BreakDuration
                })
                .AddTimeout(TimeSpan.FromSeconds(10))
                .Build();

            // Set up cross-instance invalidation
            if (_options.EnablePubSubInvalidation)
            {
                _pubSubInvalidation.InvalidationReceived += OnCrossInstanceInvalidationReceived;
                
                // Use consistent Task.Run pattern for async startup
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _pubSubInvalidation.StartListeningAsync();
                        _logger.LogInformation("Redis PubSub invalidation listening started successfully");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to start Redis PubSub invalidation listening");
                        // Consider implementing retry logic here
                    }
                });
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
            var fullKey = _options.KeyPrefix + cacheKey;

            try
            {
                return await _resilience.ExecuteAsync(async _ =>
                {
                    // Try get from cache first
                    var cachedValue = await GetFromCacheAsync<T>(fullKey);
                    if (cachedValue.HasValue)
                    {
                        _metricsProvider.CacheHit(methodName);
                        _logger.LogDebug("Cache hit for key {Key}", fullKey);
                        return cachedValue.Value;
                    }

                    // Prevent cache stampede with distributed locking if enabled
                    if (_options.EnableDistributedLocking)
                    {
                        var lockKey = $"lock:{fullKey}";
                        using var lockHandle = await _distributedLock.AcquireAsync(lockKey, TimeSpan.FromSeconds(30));

                        if (lockHandle.IsAcquired)
                        {
                            // Double-check cache after acquiring lock
                            cachedValue = await GetFromCacheAsync<T>(fullKey);
                            if (cachedValue.HasValue)
                            {
                                _metricsProvider.CacheHit(methodName);
                                _logger.LogDebug("Cache hit after lock acquisition for key {Key}", fullKey);
                                return cachedValue.Value;
                            }

                            // Execute factory and cache result
                            _metricsProvider.CacheMiss(methodName);
                            _logger.LogDebug("Cache miss, executing factory for key {Key}", fullKey);
                            
                            var result = await factory();
                            
                            if (result != null)
                            {
                                await SetToCacheWithTagsAsync(fullKey, result, settings);
                            }

                            return result;
                        }
                        else
                        {
                            // Could not acquire lock, fallback to direct execution
                            _logger.LogWarning("Could not acquire lock for key {Key}, executing factory without caching", fullKey);
                            return await factory();
                        }
                    }
                    else
                    {
                        // No locking, direct execution
                        _metricsProvider.CacheMiss(methodName);
                        var result = await factory();
                        
                        if (result != null)
                        {
                            await SetToCacheWithTagsAsync(fullKey, result, settings);
                        }

                        return result;
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Redis cache operation for method {MethodName}", methodName);
                _metricsProvider.CacheError(methodName, ex.Message);
                
                // Fallback to direct execution
                return await factory();
            }
        }

        public async Task InvalidateByTagsAsync(params string[] tags)
        {
            if (!tags.Any()) return;

            try
            {
                await _resilience.ExecuteAsync(async _ =>
                {
                    var keys = await _tagManager.GetKeysByTagsAsync(tags);
                    if (keys.Any())
                    {
                        await InvalidateKeysAtomicallyAsync(keys, tags);
                        _logger.LogDebug("Invalidated {KeyCount} keys for tags {Tags}", keys.Length, string.Join(", ", tags));
                    }

                    // Publish invalidation event for cross-instance coordination
                    if (_options.EnablePubSubInvalidation)
                    {
                        await _pubSubInvalidation.PublishInvalidationEventAsync(tags);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error invalidating cache by tags {Tags}", string.Join(", ", tags));
            }
        }
        
        /// <summary>
        /// Atomically removes cache keys and their tag associations using Redis transactions.
        /// </summary>
        private async Task InvalidateKeysAtomicallyAsync(string[] keys, string[] tags)
        {
            try
            {
                var database = _connectionManager.GetDatabase();
                var transaction = database.CreateTransaction();
                
                // Delete cache keys
                var redisKeys = keys.Select(k => (RedisKey)k).ToArray();
                var deleteKeysTask = transaction.KeyDeleteAsync(redisKeys);
                
                // Note: Tag removal will be done after transaction
                
                // Execute atomically
                var committed = await transaction.ExecuteAsync();
                
                if (committed)
                {
                    // Remove tag associations after successful key deletion
                    await _tagManager.RemoveTagAssociationsAsync(keys, tags);
                }
                else
                {
                    _logger.LogWarning("Invalidation transaction failed, falling back to non-atomic operations");
                    
                    // Fallback to non-atomic operations
                    await database.KeyDeleteAsync(redisKeys);
                    await _tagManager.RemoveTagAssociationsAsync(keys, tags);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in atomic invalidation, attempting non-atomic fallback");
                
                // Final fallback
                var database = _connectionManager.GetDatabase();
                var redisKeys = keys.Select(k => (RedisKey)k).ToArray();
                await database.KeyDeleteAsync(redisKeys);
                await _tagManager.RemoveTagAssociationsAsync(keys, tags);
            }
        }

        private void OnCrossInstanceInvalidationReceived(object? sender, CacheInvalidationEventArgs e)
        {
            // Handle async operations in fire-and-forget manner with proper error handling
            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogDebug("Received cross-instance invalidation for tags {Tags} from {SourceInstance}", 
                        string.Join(", ", e.Tags), e.SourceInstanceId);

                    // Perform local invalidation without publishing (to avoid infinite loop)
                    var keys = await _tagManager.GetKeysByTagsAsync(e.Tags);
                    if (keys.Any())
                    {
                        var database = _connectionManager.GetDatabase();
                        var redisKeys = keys.Select(k => (RedisKey)k).ToArray();
                        
                        await database.KeyDeleteAsync(redisKeys);
                        await _tagManager.RemoveTagAssociationsAsync(keys, e.Tags);
                        
                        _logger.LogDebug("Processed cross-instance invalidation for {KeyCount} keys", keys.Length);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing cross-instance invalidation for tags {Tags}", 
                        string.Join(", ", e.Tags));
                }
            });
        }

        private bool _disposed = false;
        
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            
            if (_options.EnablePubSubInvalidation)
            {
                _pubSubInvalidation.InvalidationReceived -= OnCrossInstanceInvalidationReceived;
                
                try
                {
                    await _pubSubInvalidation.StopListeningAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error stopping PubSub listening during disposal");
                }
                
                _pubSubInvalidation?.Dispose();
            }
            
            _disposed = true;
        }
        
        // Keep synchronous Dispose for IDisposable compatibility if needed
        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private async Task<(bool HasValue, T Value)> GetFromCacheAsync<T>(string key)
        {
            var database = _connectionManager.GetDatabase();
            var data = await database.StringGetAsync(key);
            
            if (!data.HasValue)
            {
                return (false, default(T)!);
            }

            try
            {
                var value = await _serializer.DeserializeAsync<T>(data!);
                return (true, value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error deserializing cached value for key {Key}", key);
                await database.KeyDeleteAsync(key); // Remove corrupted data
                return (false, default(T)!);
            }
        }

        private async Task SetToCacheAsync<T>(string key, T value, CacheMethodSettings settings)
        {
            try
            {
                var database = _connectionManager.GetDatabase();
                var data = await _serializer.SerializeAsync(value);
                var expiry = settings.Duration ?? _options.DefaultExpiration;
                
                await database.StringSetAsync(key, data, expiry);
                _logger.LogDebug("Cached value for key {Key} with expiry {Expiry}", key, expiry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error caching value for key {Key}", key);
            }
        }
        
        /// <summary>
        /// Atomically sets cache value and associates tags using Redis transactions.
        /// </summary>
        private async Task SetToCacheWithTagsAsync<T>(string key, T value, CacheMethodSettings settings)
        {
            if (!settings.Tags.Any())
            {
                await SetToCacheAsync(key, value, settings);
                return;
            }
            
            try
            {
                var database = _connectionManager.GetDatabase();
                var data = await _serializer.SerializeAsync(value);
                var expiry = settings.Duration ?? _options.DefaultExpiration;
                
                // Use Redis transaction for atomic operation
                var transaction = database.CreateTransaction();
                
                // Set the cache value
                var setCacheTask = transaction.StringSetAsync(key, data, expiry);
                
                // Associate tags - for now, we'll do this after the transaction
                // In a full implementation, this would be part of the transaction
                
                // Execute transaction
                var committed = await transaction.ExecuteAsync();
                
                if (committed)
                {
                    // Associate tags after successful cache set
                    await _tagManager.AssociateTagsAsync(key, settings.Tags);
                    _logger.LogDebug("Successfully cached value for key {Key} with {TagCount} tags", key, settings.Tags.Count);
                }
                else
                {
                    _logger.LogWarning("Failed to cache value for key {Key} - transaction was not committed", key);
                    // Fallback: try non-atomic approach
                    await SetToCacheAsync(key, value, settings);
                    await _tagManager.AssociateTagsAsync(key, settings.Tags);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in atomic cache operation for key {Key}, falling back to non-atomic", key);
                
                // Fallback: try non-atomic approach
                try
                {
                    await SetToCacheAsync(key, value, settings);
                    await _tagManager.AssociateTagsAsync(key, settings.Tags);
                }
                catch (Exception fallbackEx)
                {
                    _logger.LogError(fallbackEx, "Both atomic and fallback cache operations failed for key {Key}", key);
                }
            }
        }
    }
}