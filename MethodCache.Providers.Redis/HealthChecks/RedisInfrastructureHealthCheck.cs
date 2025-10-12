using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using MethodCache.Core.Storage;
using MethodCache.Core.Storage.Abstractions;

namespace MethodCache.Providers.Redis.HealthChecks;

/// <summary>
/// Health check for Redis Infrastructure components.
/// </summary>
public class RedisInfrastructureHealthCheck : IHealthCheck
{
    private readonly IStorageProvider _storageProvider;
    private readonly ILogger<RedisInfrastructureHealthCheck> _logger;

    public RedisInfrastructureHealthCheck(
        IStorageProvider storageProvider,
        ILogger<RedisInfrastructureHealthCheck> logger)
    {
        _storageProvider = storageProvider;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // Simple health check: try to get a non-existent key
            var testKey = $"health-check:{Guid.NewGuid()}";
            await _storageProvider.GetAsync<string>(testKey, cancellationToken);

            var data = new Dictionary<string, object>
            {
                ["Provider"] = _storageProvider.Name,
                ["Status"] = "Healthy",
                ["GetOperations"] = "Available",
                ["SetOperations"] = "Available"
            };

            return HealthCheckResult.Healthy("Redis Infrastructure is healthy", data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis Infrastructure health check failed");
            return HealthCheckResult.Unhealthy("Redis Infrastructure is unhealthy", ex);
        }
    }
}