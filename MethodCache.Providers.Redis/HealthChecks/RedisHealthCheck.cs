using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Providers.Redis.Configuration;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.HealthChecks
{
    public class RedisHealthCheck : IHealthCheck
    {
        private readonly IRedisConnectionManager _connectionManager;
        private readonly RedisOptions _options;
        private readonly ILogger<RedisHealthCheck> _logger;

        public RedisHealthCheck(
            IRedisConnectionManager connectionManager,
            IOptions<RedisOptions> options,
            ILogger<RedisHealthCheck> logger)
        {
            _connectionManager = connectionManager;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                var data = new Dictionary<string, object>();

                // Test basic connectivity
                var database = _connectionManager.GetDatabase();
                var pingResult = await database.PingAsync();
                data.Add("ping_ms", pingResult.TotalMilliseconds);

                // Test basic operations
                var testKey = $"{_options.KeyPrefix}health_check_{Guid.NewGuid():N}";
                var testValue = $"health_check_{DateTimeOffset.UtcNow:O}";

                // Test SET operation
                var setResult = await database.StringSetAsync(testKey, testValue, TimeSpan.FromSeconds(10));
                data.Add("set_success", setResult);

                if (setResult)
                {
                    // Test GET operation
                    var getValue = await database.StringGetAsync(testKey);
                    var getSuccess = getValue.HasValue && getValue == testValue;
                    data.Add("get_success", getSuccess);

                    // Test DELETE operation
                    var deleteResult = await database.KeyDeleteAsync(testKey);
                    data.Add("delete_success", deleteResult);

                    if (!getSuccess)
                    {
                        return HealthCheckResult.Degraded("Redis GET operation failed", null, data);
                    }
                }
                else
                {
                    return HealthCheckResult.Degraded("Redis SET operation failed", null, data);
                }

                // Test connection info
                var connectionInfo = await GetConnectionInfoAsync();
                foreach (var info in connectionInfo)
                {
                    data.Add(info.Key, info.Value);
                }

                // Health check passed
                _logger.LogDebug("Redis health check passed (ping: {PingMs}ms)", pingResult.TotalMilliseconds);
                return HealthCheckResult.Healthy("Redis is healthy", data);
            }
            catch (RedisConnectionException ex)
            {
                _logger.LogError(ex, "Redis health check failed - connection error");
                return HealthCheckResult.Unhealthy("Redis connection failed", ex);
            }
            catch (RedisTimeoutException ex)
            {
                _logger.LogError(ex, "Redis health check failed - timeout error");
                return HealthCheckResult.Degraded("Redis timeout", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Redis health check failed - unexpected error");
                return HealthCheckResult.Unhealthy("Redis health check failed", ex);
            }
        }

        private async Task<Dictionary<string, object>> GetConnectionInfoAsync()
        {
            var info = new Dictionary<string, object>();

            try
            {
                var connectionManager = _connectionManager as RedisConnectionManager;
                if (connectionManager != null)
                {
                    var isConnected = await connectionManager.IsConnectedAsync();
                    info.Add("connection_status", isConnected ? "Connected" : "Disconnected");
                }

                // Add configuration info
                info.Add("database_number", _options.DatabaseNumber);
                info.Add("key_prefix", _options.KeyPrefix);
                info.Add("default_expiration_minutes", _options.DefaultExpiration.TotalMinutes);
                info.Add("circuit_breaker_enabled", true);
                info.Add("distributed_locking_enabled", _options.EnableDistributedLocking);
                info.Add("pubsub_invalidation_enabled", _options.EnablePubSubInvalidation);
                info.Add("cache_warming_enabled", _options.EnableCacheWarming);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error getting Redis connection info for health check");
                info.Add("connection_info_error", ex.Message);
            }

            return info;
        }
    }
}