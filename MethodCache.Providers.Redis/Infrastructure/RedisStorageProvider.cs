using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Infrastructure.Abstractions;
using MethodCache.Providers.Redis.Configuration;
using MethodCache.Providers.Redis.Features;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using StackExchange.Redis;
using System.Collections.Concurrent;

namespace MethodCache.Providers.Redis.Infrastructure;

/// <summary>
/// Redis implementation of IStorageProvider that leverages existing Redis infrastructure components.
/// Provides distributed storage capabilities with compression, resilience, and pub/sub coordination.
/// </summary>
public class RedisStorageProvider : IStorageProvider, IAsyncDisposable
{
    private readonly IRedisConnectionManager _connectionManager;
    private readonly IRedisSerializer _serializer;
    private readonly IRedisTagManager _tagManager;
    private readonly IBackplane? _backplane;
    private readonly ResiliencePipeline _resilience;
    private readonly RedisOptions _options;
    private readonly ILogger<RedisStorageProvider> _logger;

    // Statistics
    private long _getOperations;
    private long _setOperations;
    private long _removeOperations;
    private long _errorCount;
    private readonly ConcurrentDictionary<string, (long Ticks, double Duration)> _operationTimes = new();

    // Disposal tracking
    private bool _disposed;

    public string Name => "Redis";

    public RedisStorageProvider(
        IRedisConnectionManager connectionManager,
        IRedisSerializer serializer,
        IRedisTagManager tagManager,
        IBackplane? backplane,
        IOptions<RedisOptions> options,
        ILogger<RedisStorageProvider> logger)
    {
        _connectionManager = connectionManager;
        _serializer = serializer;
        _tagManager = tagManager;
        _backplane = backplane;
        _options = options.Value;
        _logger = logger;

        // Build resilience pipeline using Redis options
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
                    _logger.LogWarning("Redis storage retry attempt {AttemptNumber}: {Exception}",
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
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var start = DateTimeOffset.UtcNow;
        Interlocked.Increment(ref _getOperations);

        try
        {
            return await _resilience.ExecuteAsync(async _ =>
            {
                var database = _connectionManager.GetDatabase();
                var fullKey = _options.KeyPrefix + key;
                var data = await database.StringGetAsync(fullKey).ConfigureAwait(false);

                if (!data.HasValue)
                {
                    _logger.LogDebug("Cache miss for key {Key}", fullKey);
                    return default(T);
                }

                try
                {
                    var bytes = (byte[])data!;
                    var value = await _serializer.DeserializeAsync<T>(bytes).ConfigureAwait(false);
                    _logger.LogDebug("Cache hit for key {Key}", fullKey);
                    return value;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error deserializing cached value for key {Key}, removing corrupted data", fullKey);
                    await database.KeyDeleteAsync(fullKey); // Remove corrupted data
                    return default(T);
                }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            _logger.LogError(ex, "Error getting value for key {Key}", key);
            return default(T);
        }
        finally
        {
            RecordOperationTime("Get", start);
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        await SetAsync(key, value, expiration, Enumerable.Empty<string>(), cancellationToken);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        var start = DateTimeOffset.UtcNow;
        Interlocked.Increment(ref _setOperations);

        try
        {
            await _resilience.ExecuteAsync(async _ =>
            {
                var database = _connectionManager.GetDatabase();
                var fullKey = _options.KeyPrefix + key;
                var data = await _serializer.SerializeAsync(value);
                var tagArray = tags.ToArray();

                if (tagArray.Length > 0)
                {
                    // Use atomic operation for cache set + tag association
                    await SetCacheWithTagsAtomicallyAsync(database, fullKey, data, expiration, tagArray);
                    _logger.LogDebug("Set key {Key} with {TagCount} tags atomically", fullKey, tagArray.Length);
                }
                else
                {
                    // Simple set without tags
                    await database.StringSetAsync(fullKey, data, expiration);
                    _logger.LogDebug("Set key {Key} with expiration {Expiration}", fullKey, expiration);
                }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            _logger.LogError(ex, "Error setting value for key {Key}", key);
        }
        finally
        {
            RecordOperationTime("Set", start);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var start = DateTimeOffset.UtcNow;
        Interlocked.Increment(ref _removeOperations);

        try
        {
            await _resilience.ExecuteAsync(async _ =>
            {
                var database = _connectionManager.GetDatabase();
                var fullKey = _options.KeyPrefix + key;

                // Remove the key and its tag associations
                await database.KeyDeleteAsync(fullKey);
                await _tagManager.RemoveAllTagAssociationsAsync(fullKey);

                _logger.LogDebug("Removed key {Key} and its tag associations", fullKey);
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            _logger.LogError(ex, "Error removing key {Key}", key);
        }
        finally
        {
            RecordOperationTime("Remove", start);
        }
    }

    public async Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        var start = DateTimeOffset.UtcNow;

        try
        {
            await _resilience.ExecuteAsync(async _ =>
            {
                // Get keys associated with this tag
                var keys = await _tagManager.GetKeysByTagsAsync(new[] { tag });
                if (keys.Length == 0)
                {
                    _logger.LogDebug("No keys found for tag {Tag}", tag);
                    return;
                }

                // Remove keys atomically
                await InvalidateKeysAtomicallyAsync(keys, new[] { tag });
                _logger.LogDebug("Removed {KeyCount} keys for tag {Tag}", keys.Length, tag);

                // Publish invalidation event for cross-instance coordination
                if (_options.EnablePubSubInvalidation && _backplane != null)
                {
                    await _backplane.PublishTagInvalidationAsync(tag, cancellationToken);
                }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            _logger.LogError(ex, "Error removing keys by tag {Tag}", tag);
        }
        finally
        {
            RecordOperationTime("RemoveByTag", start);
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _resilience.ExecuteAsync(async _ =>
            {
                var database = _connectionManager.GetDatabase();
                var fullKey = _options.KeyPrefix + key;
                return await database.KeyExistsAsync(fullKey);
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            _logger.LogError(ex, "Error checking existence of key {Key}", key);
            return false;
        }
    }

    public async Task<HealthStatus> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var database = _connectionManager.GetDatabase();

            // Perform a simple ping operation with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var ping = await database.PingAsync();

            if (ping.TotalMilliseconds > 1000) // > 1 second is concerning
            {
                _logger.LogWarning("Redis ping took {PingTime}ms, performance may be degraded", ping.TotalMilliseconds);
                return HealthStatus.Degraded;
            }

            return HealthStatus.Healthy;
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("Redis health check timed out");
            return HealthStatus.Unhealthy;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis health check failed");
            return HealthStatus.Unhealthy;
        }
    }

    public async Task<StorageStats?> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var database = _connectionManager.GetDatabase();
            var info = await database.ExecuteAsync("INFO", "stats");

            var getOps = Interlocked.Read(ref _getOperations);
            var setOps = Interlocked.Read(ref _setOperations);
            var removeOps = Interlocked.Read(ref _removeOperations);
            var errors = Interlocked.Read(ref _errorCount);

            // Calculate average response time
            var avgResponseTime = CalculateAverageResponseTime();

            return new StorageStats
            {
                GetOperations = getOps,
                SetOperations = setOps,
                RemoveOperations = removeOps,
                AverageResponseTimeMs = avgResponseTime,
                ErrorCount = errors,
                AdditionalStats = new Dictionary<string, object>
                {
                    ["RedisInfo"] = info.ToString(),
                    ["ConnectionString"] = MaskConnectionString(_options.ConnectionString),
                    ["DatabaseNumber"] = _options.DatabaseNumber,
                    ["KeyPrefix"] = _options.KeyPrefix,
                    ["SerializerType"] = _options.DefaultSerializer.ToString(),
                    ["CompressionType"] = _options.Compression.ToString(),
                    ["PubSubEnabled"] = _options.EnablePubSubInvalidation,
                    ["DistributedLockingEnabled"] = _options.EnableDistributedLocking
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Redis storage stats");
            return new StorageStats
            {
                GetOperations = Interlocked.Read(ref _getOperations),
                SetOperations = Interlocked.Read(ref _setOperations),
                RemoveOperations = Interlocked.Read(ref _removeOperations),
                ErrorCount = Interlocked.Read(ref _errorCount),
                AverageResponseTimeMs = CalculateAverageResponseTime()
            };
        }
    }

    /// <summary>
    /// Atomically sets cache value and associates tags using a Lua script.
    /// </summary>
    private async Task SetCacheWithTagsAtomicallyAsync(IDatabase database, string cacheKey, byte[] data,
        TimeSpan expiry, string[] tags)
    {
        if (tags.Length == 0) return;

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
        scriptArgs.AddRange(tags.Select(tag => (RedisValue)tag)); // ARGV[3+] - tags

        var keys = new RedisKey[]
        {
            cacheKey,           // KEYS[1] - cache key
            _options.KeyPrefix  // KEYS[2] - key prefix for tag keys
        };

        await database.ScriptEvaluateAsync(luaScript, keys, scriptArgs.ToArray());
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

    private void RecordOperationTime(string operation, DateTimeOffset start)
    {
        var duration = (DateTimeOffset.UtcNow - start).TotalMilliseconds;
        _operationTimes.AddOrUpdate(operation,
            (start.Ticks, duration),
            (key, existing) => (DateTimeOffset.UtcNow.Ticks, (existing.Duration + duration) / 2));
    }

    private double CalculateAverageResponseTime()
    {
        if (_operationTimes.IsEmpty)
            return 0.0;

        return _operationTimes.Values.Average(x => x.Duration);
    }

    private static string MaskConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return "[not set]";

        // Simple masking for security - hide passwords
        return connectionString.Contains("password", StringComparison.OrdinalIgnoreCase)
            ? connectionString.Substring(0, Math.Min(20, connectionString.Length)) + "..."
            : connectionString;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        try
        {
            // The Redis connection and other services are managed by DI container
            // We just need to clean up our own resources
            _operationTimes.Clear();
            _logger.LogDebug("RedisStorageProvider disposed");
        }
        finally
        {
            _disposed = true;
        }

        return ValueTask.CompletedTask;
    }
}