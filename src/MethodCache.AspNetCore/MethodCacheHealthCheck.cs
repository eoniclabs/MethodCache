using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MethodCache.AspNetCore;

/// <summary>
/// Reports whether the cache answers and includes its statistics in the health report. Degraded while the
/// distributed cache is unreachable, since MethodCache then serves from memory only.
/// </summary>
public sealed class MethodCacheHealthCheck : IHealthCheck
{
    private readonly ICacheAdministrator _admin;

    /// <summary>
    /// Creates the health check for the cache that <paramref name="admin"/> inspects.
    /// </summary>
    /// <param name="admin">The cache administrator registered by <c>AddMethodCache()</c>.</param>
    public MethodCacheHealthCheck(ICacheAdministrator admin)
    {
        _admin = admin ?? throw new ArgumentNullException(nameof(admin));
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var stats = await _admin.GetStatsAsync(cancellationToken).ConfigureAwait(false);
            var data = new Dictionary<string, object>
            {
                ["entries"] = stats.Entries,
                ["hitRatio"] = stats.HitRatio,
                ["evictions"] = stats.Evictions,
                ["memoryUsageBytes"] = stats.MemoryUsage
            };

            var queues = _admin.GetInvalidationQueues();
            data["invalidationQueued"] = queues.Distributed;
            data["publicationQueued"] = queues.Publication;
            var distributed = await _admin.CheckDistributedCacheAsync(cancellationToken).ConfigureAwait(false);
            if (distributed.HasValue)
            {
                data["distributedCacheAvailable"] = distributed.Value;
            }

            return distributed == false || queues.Distributed != 0 || queues.Publication != 0
                ? HealthCheckResult.Degraded("Distributed invalidation is unavailable or recovering; inspect the recovery queue counters.", data: data)
                : HealthCheckResult.Healthy("MethodCache is available.", data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "MethodCache did not answer.", ex);
        }
    }
}
