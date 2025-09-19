using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Core.Options;
using MethodCache.Providers.Redis.Configuration;
using MethodCache.Providers.Redis.Features;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
                .AddRetry(new RetryStrategyOptions()
                {
                    MaxRetryAttempts = _options.Retry.MaxRetries,
                    BackoffType = _options.Retry.BackoffType switch
                    {
                        RetryBackoffType.Linear => DelayBackoffType.Linear,
                        RetryBackoffType.Exponential => DelayBackoffType.Exponential,
                        _ => DelayBackoffType.Exponential
                    },
                    Delay = _options.Retry.BaseDelay,
                    OnRetry = args =>
                    {
                        _logger.LogWarning("Redis operation retry attempt {AttemptNumber}: {Exception}",
                            args.AttemptNumber, args.Outcome.Exception?.Message);
                        return ValueTask.CompletedTask;
                    }
                })
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
            var stampede = settings.StampedeProtection;
            var distributedLockOptions = settings.DistributedLock;

            if (distributedLockOptions == null && stampede?.Mode == StampedeProtectionMode.DistributedLock)
            {
                distributedLockOptions = new DistributedLockOptions(stampede.RefreshAheadWindow ?? TimeSpan.FromSeconds(30), 1);
            }

            var useDistributedLock = distributedLockOptions != null || _options.EnableDistributedLocking;
            var lockTimeout = distributedLockOptions?.Timeout ?? TimeSpan.FromSeconds(30);

            try
            {
                return await _resilience.ExecuteAsync(async _ =>
                {
                    // Try get from cache first
                    var cachedValue = await GetFromCacheAsync<T>(fullKey);
                    if (cachedValue.HasValue && !ShouldRefreshAhead(cachedValue.TimeToLive, settings))
                    {
                        _metricsProvider.CacheHit(methodName);
                        _logger.LogDebug("Cache hit for key {Key}", fullKey);
                        return cachedValue.Value;
                    }

                    // Prevent cache stampede with distributed locking if enabled
                    if (useDistributedLock)
                    {
                        var lockKey = $"lock:{fullKey}";
                        if (distributedLockOptions?.MaxConcurrency > 1)
                        {
                            _logger.LogWarning("Redis distributed lock currently supports a single writer. MaxConcurrency {MaxConcurrency} will be treated as 1.", distributedLockOptions.MaxConcurrency);
                        }

                        using var lockHandle = await _distributedLock.AcquireAsync(lockKey, lockTimeout);

                        if (lockHandle.IsAcquired)
                        {
                            // Double-check cache after acquiring lock
                            cachedValue = await GetFromCacheAsync<T>(fullKey);
                            if (cachedValue.HasValue && !ShouldRefreshAhead(cachedValue.TimeToLive, settings))
                            {
                                _metricsProvider.CacheHit(methodName);
                                _logger.LogDebug("Cache hit after lock acquisition for key {Key}", fullKey);
                                return cachedValue.Value;
                            }

                            // Execute factory and cache result with automatic lock renewal
                            _metricsProvider.CacheMiss(methodName);
                            _logger.LogDebug("Cache miss, executing factory for key {Key}", fullKey);
                            
                            var result = await ExecuteFactoryWithLockRenewalAsync(factory, lockHandle, lockTimeout);
                            
                            if (result != null)
                            {
                                await SetToCacheWithTagsAsync(fullKey, result, settings);
                            }

                            return result;
                        }
                        else
                        {
                            // Could not acquire lock, wait and retry to get cached value
                            _logger.LogDebug("Could not acquire lock for key {Key}, waiting for other thread to complete", fullKey);
                            
                            // Wait briefly for the lock holder to complete and cache the result
                            await Task.Delay(100);
                            
                            // Retry getting from cache
                            cachedValue = await GetFromCacheAsync<T>(fullKey);
                            if (cachedValue.HasValue && !ShouldRefreshAhead(cachedValue.TimeToLive, settings))
                            {
                                _metricsProvider.CacheHit(methodName);
                                _logger.LogDebug("Cache hit after waiting for lock release for key {Key}", fullKey);
                                return cachedValue.Value;
                            }
                            
                            // If still no cached value, try to acquire lock one more time
                            using var retryLockHandle = await _distributedLock.AcquireAsync(lockKey, lockTimeout);
                            if (retryLockHandle.IsAcquired)
                            {
                                // Final double-check after retry lock acquisition
                                cachedValue = await GetFromCacheAsync<T>(fullKey);
                                if (cachedValue.HasValue && !ShouldRefreshAhead(cachedValue.TimeToLive, settings))
                                {
                                    _metricsProvider.CacheHit(methodName);
                                    return cachedValue.Value;
                                }
                                
                                // Execute factory and cache result (retry path) with automatic lock renewal
                                _metricsProvider.CacheMiss(methodName);
                                var result = await ExecuteFactoryWithLockRenewalAsync(factory, retryLockHandle, lockTimeout);
                                
                                if (result != null)
                                {
                                    await SetToCacheWithTagsAsync(fullKey, result, settings);
                                }
                                
                                return result;
                            }
                            else
                            {
                                // Final fallback - execute without caching but log warning
                                _logger.LogWarning("Could not acquire lock after retry for key {Key}, executing factory without caching", fullKey);
                                return await factory();
                            }
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

        public async Task InvalidateByKeysAsync(params string[] keys)
        {
            if (keys == null || keys.Length == 0)
            {
                return;
            }

            var normalizedKeys = keys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => _options.KeyPrefix + k)
                .ToArray();

            if (normalizedKeys.Length == 0)
            {
                return;
            }

            try
            {
                await _resilience.ExecuteAsync(async _ =>
                {
                    var database = _connectionManager.GetDatabase();
                    var redisKeys = normalizedKeys.Select(k => (RedisKey)k).ToArray();
                    if (redisKeys.Length > 0)
                    {
                        await database.KeyDeleteAsync(redisKeys).ConfigureAwait(false);
                    }

                    var tagTasks = normalizedKeys.Select(key => _tagManager.RemoveAllTagAssociationsAsync(key));
                    await Task.WhenAll(tagTasks).ConfigureAwait(false);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error invalidating cache by keys {Keys}", string.Join(", ", keys));
                throw;
            }
        }

        public async Task InvalidateByTagPatternAsync(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return;
            }

            try
            {
                await _resilience.ExecuteAsync(async _ =>
                {
                    var database = _connectionManager.GetDatabase();
                    var prefixedPattern = _options.KeyPrefix + pattern;
                    var keysToDelete = new List<RedisKey>();
                    var multiplexer = database.Multiplexer;

                    foreach (var endpoint in multiplexer.GetEndPoints())
                    {
                        try
                        {
                            var server = multiplexer.GetServer(endpoint);
                            if (!server.IsConnected)
                            {
                                _logger.LogWarning("Redis server at endpoint {Endpoint} is not connected. Skipping.", endpoint);
                                continue;
                            }

                            var scan = server.Keys(database.Database, pattern: prefixedPattern);
                            keysToDelete.AddRange(scan);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error scanning keys on endpoint {Endpoint}. Continuing with other endpoints.", endpoint);
                            // Continue with other endpoints even if one fails
                        }
                    }

                    if (keysToDelete.Count == 0)
                    {
                        return;
                    }

                    await database.KeyDeleteAsync(keysToDelete.ToArray()).ConfigureAwait(false);
                    var tagTasks = keysToDelete.Select(key => _tagManager.RemoveAllTagAssociationsAsync(key!));
                    await Task.WhenAll(tagTasks).ConfigureAwait(false);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error invalidating cache by pattern {Pattern}", pattern);
                throw;
            }
        }

        public async ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator)
        {
            var key = keyGenerator.GenerateKey(methodName, args, settings);
            
            try
            {
                return await _resilience.ExecuteAsync(async _ =>
                {
                    var database = _connectionManager.GetDatabase();
                    var data = await database.StringGetAsync(key).ConfigureAwait(false);
                    
                    if (!data.HasValue)
                        return default(T);
                    
                    // Deserialize (serializer handles decompression internally)
                    return _serializer.Deserialize<T>(data!);
                });
            }
            catch (Exception)
            {
                // For read-only operations, return default on any error
                return default(T);
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
                    _logger.LogWarning("Invalidation transaction failed, falling back to pipelined operations");
                    
                    // Fallback using pipelining for better performance
                    var batch = database.CreateBatch();
                    var deleteTask = batch.KeyDeleteAsync(redisKeys);
                    batch.Execute();
                    await deleteTask;
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
        
        // Keep synchronous Dispose for IDisposable compatibility - use non-blocking pattern
        public void Dispose()
        {
            if (_disposed) return;
            
            try
            {
                // Use a timeout to prevent blocking indefinitely
                var disposeTask = DisposeAsync().AsTask();
                if (!disposeTask.Wait(TimeSpan.FromSeconds(5)))
                {
                    _logger.LogWarning("DisposeAsync timed out during synchronous disposal, continuing with forced cleanup");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during synchronous disposal");
            }
            finally
            {
                _disposed = true;
            }
        }

        private async Task<(bool HasValue, T Value, TimeSpan? TimeToLive)> GetFromCacheAsync<T>(string key)
        {
            return await _resilience.ExecuteAsync(async _ =>
            {
                var database = _connectionManager.GetDatabase();
                var data = await database.StringGetAsync(key).ConfigureAwait(false);

                if (!data.HasValue)
                {
                    return (false, default(T)!, null);
                }

                try
                {
                    var bytes = (byte[])data!;
                    if (bytes == null)
                    {
                        return (false, default(T)!, null);
                    }

                    var value = await _serializer.DeserializeAsync<T>(bytes).ConfigureAwait(false);
                    var ttl = await database.KeyTimeToLiveAsync(key).ConfigureAwait(false);
                    return (true, value, ttl);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error deserializing cached value for key {Key}", key);
                    await database.KeyDeleteAsync(key); // Remove corrupted data
                    return (false, default(T)!, null);
                }
            });
        }

        private static bool ShouldRefreshAhead(TimeSpan? timeToLive, CacheMethodSettings settings)
        {
            if (timeToLive == null)
            {
                return false;
            }

            var configuredRefreshAhead = settings.RefreshAhead;
            if (configuredRefreshAhead is TimeSpan refreshAhead && refreshAhead > TimeSpan.Zero && timeToLive <= refreshAhead)
            {
                return true;
            }

            var stampede = settings.StampedeProtection;
            if (stampede == null)
            {
                return false;
            }

            switch (stampede.Mode)
            {
                case StampedeProtectionMode.RefreshAhead:
                    var window = stampede.RefreshAheadWindow ?? configuredRefreshAhead;
                    return window.HasValue && window.Value > TimeSpan.Zero && timeToLive <= window;
                case StampedeProtectionMode.Probabilistic:
                {
                    var duration = settings.Duration ?? TimeSpan.Zero;
                    if (duration <= TimeSpan.Zero)
                    {
                        return false;
                    }

                    var remainingRatio = Math.Clamp(timeToLive.Value.TotalSeconds / duration.TotalSeconds, 0d, 1d);
                    var beta = stampede.Beta <= 0 ? 1d : stampede.Beta;
                    var probability = Math.Exp(-beta * (1 - remainingRatio));
                    var sample = Random.Shared.NextDouble();
                    return sample > probability;
                }
                default:
                    return false;
            }
        }

        private async Task SetToCacheAsync<T>(string key, T value, CacheMethodSettings settings)
        {
            await _resilience.ExecuteAsync(async _ =>
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
                    throw; // Re-throw to allow retry policy to handle it
                }
            });
        }
        
        /// <summary>
        /// Atomically sets cache value and associates tags using a Lua script for true atomicity.
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
                
                // Use Lua script for truly atomic cache set + tag association
                await SetCacheWithTagsAtomicallyAsync(database, key, data, expiry, settings.Tags);
                
                _logger.LogDebug("Successfully cached value for key {Key} with {TagCount} tags atomically", 
                    key, settings.Tags.Count);
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

        /// <summary>
        /// Atomically sets cache value and associates tags using a Lua script.
        /// This ensures that either both operations succeed or both fail, preventing partial state.
        /// </summary>
        private async Task SetCacheWithTagsAtomicallyAsync(IDatabase database, string cacheKey, byte[] data, 
            TimeSpan expiry, IEnumerable<string> tags)
        {
            var tagArray = tags.ToArray();
            if (tagArray.Length == 0) return;

            // Lua script that atomically performs:
            // 1. SET the cache value with expiry
            // 2. SADD key to each tag set  
            // 3. SADD each tag to key's reverse lookup set
            const string luaScript = @"
                -- Set the cache value with expiry
                redis.call('SETEX', KEYS[1], ARGV[1], ARGV[2])
                
                -- Associate tags (forward mapping: tag -> keys)
                for i = 3, #ARGV do
                    if ARGV[i] ~= '' then
                        local tagKey = KEYS[2] .. 'tags:' .. ARGV[i]
                        redis.call('SADD', tagKey, KEYS[1])
                        -- Set TTL on tag set to prevent memory leaks
                        redis.call('EXPIRE', tagKey, ARGV[1] * 2)
                    end
                end
                
                -- Reverse mapping: key -> tags (for cleanup)
                if #ARGV > 2 then
                    local keyTagsKey = KEYS[2] .. 'key-tags:' .. KEYS[1]
                    for i = 3, #ARGV do
                        if ARGV[i] ~= '' then
                            redis.call('SADD', keyTagsKey, ARGV[i])
                        end
                    end
                    -- Set TTL on reverse mapping
                    redis.call('EXPIRE', keyTagsKey, ARGV[1] * 2)
                end
                
                return 1";

            // Prepare arguments: expiry (seconds), serialized data, then all tags
            var scriptArgs = new List<RedisValue>
            {
                (int)expiry.TotalSeconds,  // ARGV[1] - expiry in seconds
                data                       // ARGV[2] - serialized cache data
            };
            scriptArgs.AddRange(tagArray.Select(tag => (RedisValue)tag)); // ARGV[3+] - tags

            var keys = new RedisKey[] 
            { 
                cacheKey,           // KEYS[1] - cache key
                _options.KeyPrefix  // KEYS[2] - key prefix for tag keys
            };

            await _resilience.ExecuteAsync(async _ =>
            {
                await database.ScriptEvaluateAsync(luaScript, keys, scriptArgs.ToArray());
            });
        }

        /// <summary>
        /// Executes a factory method with automatic lock renewal to prevent locks from expiring
        /// during long-running operations, which would cause cache stampedes.
        /// </summary>
        private async Task<T> ExecuteFactoryWithLockRenewalAsync<T>(Func<Task<T>> factory, ILockHandle lockHandle, TimeSpan lockDuration)
        {
            if (!lockHandle.IsAcquired)
            {
                return await factory();
            }

            var effectiveLockDuration = lockDuration > TimeSpan.Zero ? lockDuration : TimeSpan.FromSeconds(30);
            var renewalInterval = TimeSpan.FromSeconds(Math.Max(1, effectiveLockDuration.TotalSeconds / 3));
            
            using var cts = new CancellationTokenSource();
            
            // Start background renewal task
            var renewalTask = Task.Run(async () =>
            {
                try
                {
                    while (!cts.Token.IsCancellationRequested && lockHandle.IsAcquired)
                    {
                        await Task.Delay(renewalInterval, cts.Token);
                        
                        if (!cts.Token.IsCancellationRequested && lockHandle.IsAcquired)
                        {
                            try
                            {
                                await lockHandle.RenewAsync(effectiveLockDuration);
                                _logger.LogDebug("Lock renewed for resource {Resource}", lockHandle.Resource);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to renew lock for resource {Resource}", lockHandle.Resource);
                                break; // Stop renewal attempts if one fails
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when factory completes
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in lock renewal task for resource {Resource}", lockHandle.Resource);
                }
            }, cts.Token);

            try
            {
                // Execute the factory method
                var result = await factory();
                
                // Cancel renewal and wait for it to complete
                cts.Cancel();
                try
                {
                    await renewalTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }

                return result;
            }
            catch
            {
                // Ensure renewal is cancelled even if factory throws
                cts.Cancel();
                try
                {
                    await renewalTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
                throw;
            }
        }
    }
}
