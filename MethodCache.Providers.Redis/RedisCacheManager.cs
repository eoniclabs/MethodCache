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
    public class RedisCacheManager : ICacheManager, IDisposable
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
                _ = Task.Run(async () => await _pubSubInvalidation.StartListeningAsync());
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
                                await SetToCacheAsync(fullKey, result, settings);
                                if (settings.Tags.Any())
                                {
                                    await _tagManager.AssociateTagsAsync(fullKey, settings.Tags);
                                }
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
                            await SetToCacheAsync(fullKey, result, settings);
                            if (settings.Tags.Any())
                            {
                                await _tagManager.AssociateTagsAsync(fullKey, settings.Tags);
                            }
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
                        var database = _connectionManager.GetDatabase();
                        var redisKeys = keys.Select(k => (RedisKey)k).ToArray();
                        
                        await database.KeyDeleteAsync(redisKeys);
                        await _tagManager.RemoveTagAssociationsAsync(keys, tags);
                        
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

        private async void OnCrossInstanceInvalidationReceived(object? sender, CacheInvalidationEventArgs e)
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
        }

        public void Dispose()
        {
            if (_options.EnablePubSubInvalidation)
            {
                _pubSubInvalidation.InvalidationReceived -= OnCrossInstanceInvalidationReceived;
                _pubSubInvalidation?.StopListeningAsync().GetAwaiter().GetResult();
                _pubSubInvalidation?.Dispose();
            }
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
    }
}