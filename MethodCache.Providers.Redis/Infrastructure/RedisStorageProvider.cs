using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core.Storage;
using MethodCache.Providers.Redis.Configuration;
using MethodCache.Providers.Redis.Features;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using StackExchange.Redis;
using System.Collections.Concurrent;
using MethodCache.Core.Storage.Abstractions;

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
        var retryBackoffType = _options.Retry.BackoffType switch
        {
            RetryBackoffType.Linear => DelayBackoffType.Linear,
            RetryBackoffType.ExponentialWithJitter => DelayBackoffType.Exponential,
            RetryBackoffType.Exponential => DelayBackoffType.Exponential,
            _ => DelayBackoffType.Exponential
        };
        var useJitter = _options.Retry.BackoffType == RetryBackoffType.ExponentialWithJitter;

        _resilience = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions()
            {
                MaxRetryAttempts = _options.Retry.MaxRetries,
                BackoffType = retryBackoffType,
                UseJitter = useJitter,
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

    public async ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
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
                    await database.KeyDeleteAsync(fullKey).ConfigureAwait(false); // Remove corrupted data
                    return default(T);
                }
            }, cancellationToken).ConfigureAwait(false);
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

    public ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        return SetAsync(key, value, expiration, Enumerable.Empty<string>(), cancellationToken);
    }

    public async ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        var start = DateTimeOffset.UtcNow;
        Interlocked.Increment(ref _setOperations);

        try
        {
            await _resilience.ExecuteAsync(async _ =>
            {
                var database = _connectionManager.GetDatabase();
                var fullKey = _options.KeyPrefix + key;
                var data = await _serializer.SerializeAsync(value).ConfigureAwait(false);
                var tagArray = tags.ToArray();

                if (tagArray.Length > 0)
                {
                    // Use atomic operation for cache set + tag association
                    await SetCacheWithTagsAtomicallyAsync(database, fullKey, data, expiration, tagArray).ConfigureAwait(false);
                    _logger.LogDebug("Set key {Key} with {TagCount} tags atomically", fullKey, tagArray.Length);
                }
                else
                {
                    // Simple set without tags
                    await database.StringSetAsync(fullKey, data, expiration).ConfigureAwait(false);
                    _logger.LogDebug("Set key {Key} with expiration {Expiration}", fullKey, expiration);
                }
            }, cancellationToken).ConfigureAwait(false);
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

    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var start = DateTimeOffset.UtcNow;
        Interlocked.Increment(ref _removeOperations);

        try
        {
            await _resilience.ExecuteAsync(async _ =>
            {
                var database = _connectionManager.GetDatabase();
                var fullKey = NormalizeCacheKey(key);
                await RemoveKeyWithTagsAsync(database, fullKey).ConfigureAwait(false);

                _logger.LogDebug("Atomically removed key {Key} and its tag associations using Lua script", fullKey);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            _logger.LogError(ex, "Error removing key {Key} atomically", key);
        }
        finally
        {
            RecordOperationTime("Remove", start);
        }
    }

    public async ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        var start = DateTimeOffset.UtcNow;

        try
        {
            await _resilience.ExecuteAsync(async _ =>
            {
                // Get keys associated with this tag
                var keys = await _tagManager.GetKeysByTagsAsync(new[] { tag }).ConfigureAwait(false);
                if (keys.Length == 0)
                {
                    _logger.LogDebug("No keys found for tag {Tag}", tag);
                    return;
                }

                var normalizedKeys = keys.Select(NormalizeCacheKey).ToArray();
                var database = _connectionManager.GetDatabase();
                var removalTasks = normalizedKeys.Select(k => RemoveKeyWithTagsAsync(database, k));
                await Task.WhenAll(removalTasks).ConfigureAwait(false);

                // Remove the tag set itself to avoid leaks
                var tagSetKey = $"{_options.KeyPrefix}tags:{tag}";
                await database.KeyDeleteAsync(tagSetKey).ConfigureAwait(false);

                _logger.LogDebug("Removed {KeyCount} keys for tag {Tag}", normalizedKeys.Length, tag);

                // Publish invalidation event for cross-instance coordination
                if (_options.EnablePubSubInvalidation && _backplane != null)
                {
                    await _backplane.PublishTagInvalidationAsync(tag, cancellationToken).ConfigureAwait(false);
                }
            }, cancellationToken).ConfigureAwait(false);
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

    public async ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _resilience.ExecuteAsync(async _ =>
            {
                var database = _connectionManager.GetDatabase();
                var fullKey = _options.KeyPrefix + key;
                return await database.KeyExistsAsync(fullKey).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            _logger.LogError(ex, "Error checking existence of key {Key}", key);
            return false;
        }
    }

    public async ValueTask<HealthStatus> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var database = _connectionManager.GetDatabase();

            // Perform a simple ping operation with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var ping = await database.PingAsync().ConfigureAwait(false);

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

    public async ValueTask<StorageStats?> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var database = _connectionManager.GetDatabase();
            var info = await database.ExecuteAsync("INFO", "stats").ConfigureAwait(false);

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

        const string luaScript = @"
            local ttlMs = tonumber(ARGV[1])
            local tagTtlMs = tonumber(ARGV[2])
            local payload = ARGV[3]
            local cacheKey = KEYS[1]
            local keyPrefix = KEYS[2]

            redis.call('PSETEX', cacheKey, ttlMs, payload)

            for i = 4, #ARGV do
                if ARGV[i] ~= '' then
                    local tagKey = keyPrefix .. 'tags:' .. ARGV[i]
                    redis.call('SADD', tagKey, cacheKey)
                    redis.call('PEXPIRE', tagKey, tagTtlMs)
                end
            end

            if #ARGV > 3 then
                local keyTagsKey = keyPrefix .. 'key-tags:' .. cacheKey
                for i = 4, #ARGV do
                    if ARGV[i] ~= '' then
                        redis.call('SADD', keyTagsKey, ARGV[i])
                    end
                end
                redis.call('PEXPIRE', keyTagsKey, tagTtlMs)
            end

            return 1";

        var ttlMs = Math.Max(1, (long)Math.Ceiling(expiry.TotalMilliseconds));
        var tagTtl = Math.Max(1, Math.Min(ttlMs * 2, (long)TimeSpan.FromDays(365).TotalMilliseconds));

        var scriptArgs = new RedisValue[tags.Length + 3];
        scriptArgs[0] = ttlMs;
        scriptArgs[1] = tagTtl;
        scriptArgs[2] = data;

        for (int i = 0; i < tags.Length; i++)
        {
            scriptArgs[i + 3] = tags[i];
        }

        var keys = new RedisKey[]
        {
            cacheKey,           // KEYS[1] - cache key
            _options.KeyPrefix  // KEYS[2] - key prefix for tag keys
        };

        await database.ScriptEvaluateAsync(luaScript, keys, scriptArgs).ConfigureAwait(false);
    }

    private static readonly string RemoveKeyWithTagsScript = @"
        local cacheKey = KEYS[1]
        local keyPrefix = ARGV[1]
        local keyTagsKey = keyPrefix .. 'key-tags:' .. cacheKey

        local tags = redis.call('SMEMBERS', keyTagsKey)

        for i, tag in ipairs(tags) do
            local tagKey = keyPrefix .. 'tags:' .. tag
            redis.call('SREM', tagKey, cacheKey)
        end

        redis.call('DEL', cacheKey)
        redis.call('DEL', keyTagsKey)

        return 1";

    private Task RemoveKeyWithTagsAsync(IDatabase database, string cacheKey)
    {
        var keys = new RedisKey[] { cacheKey };
        var args = new RedisValue[] { _options.KeyPrefix };
        return database.ScriptEvaluateAsync(RemoveKeyWithTagsScript, keys, args);
    }

    private string NormalizeCacheKey(string key)
    {
        return key.StartsWith(_options.KeyPrefix, StringComparison.Ordinal)
            ? key
            : _options.KeyPrefix + key;
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
