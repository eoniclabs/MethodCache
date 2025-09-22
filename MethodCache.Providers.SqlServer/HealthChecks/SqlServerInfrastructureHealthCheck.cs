using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using MethodCache.Infrastructure.Abstractions;

namespace MethodCache.Providers.SqlServer.HealthChecks;

/// <summary>
/// Health check for SQL Server Infrastructure components.
/// </summary>
public class SqlServerInfrastructureHealthCheck : IHealthCheck
{
    private readonly IStorageProvider _storageProvider;
    private readonly ILogger<SqlServerInfrastructureHealthCheck> _logger;

    public SqlServerInfrastructureHealthCheck(
        IStorageProvider storageProvider,
        ILogger<SqlServerInfrastructureHealthCheck> logger)
    {
        _storageProvider = storageProvider;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // Perform health check on the storage provider
            var healthStatus = await _storageProvider.GetHealthAsync(cancellationToken);

            var data = new Dictionary<string, object>
            {
                ["Provider"] = _storageProvider.Name,
                ["Status"] = healthStatus.ToString()
            };

            // Get additional statistics if available
            try
            {
                var stats = await _storageProvider.GetStatsAsync(cancellationToken);
                if (stats != null)
                {
                    data["GetOperations"] = stats.GetOperations;
                    data["SetOperations"] = stats.SetOperations;
                    data["RemoveOperations"] = stats.RemoveOperations;
                    data["ErrorCount"] = stats.ErrorCount;
                    data["AverageResponseTimeMs"] = stats.AverageResponseTimeMs;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get storage provider statistics during health check");
                data["StatsError"] = ex.Message;
            }

            return healthStatus switch
            {
                HealthStatus.Healthy => HealthCheckResult.Healthy("SQL Server Infrastructure is healthy", data),
                HealthStatus.Degraded => HealthCheckResult.Degraded("SQL Server Infrastructure is degraded", null, data),
                _ => HealthCheckResult.Unhealthy("SQL Server Infrastructure is unhealthy", null, data)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SQL Server Infrastructure health check failed");

            var data = new Dictionary<string, object>
            {
                ["Provider"] = _storageProvider.Name,
                ["Error"] = ex.Message
            };

            return HealthCheckResult.Unhealthy("SQL Server Infrastructure health check failed", ex, data);
        }
    }
}