using MethodCache.Core.Management;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MethodCache.AspNetCore;

/// <summary>Reports whether the cache answers and includes its statistics in the health report.</summary>
public sealed class MethodCacheHealthCheck : IHealthCheck
{
    private readonly ICacheAdministrator _admin;

    public MethodCacheHealthCheck(ICacheAdministrator admin)
    {
        _admin = admin ?? throw new ArgumentNullException(nameof(admin));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var stats = await _admin.GetStatsAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("MethodCache is available.", new Dictionary<string, object>
            {
                ["entries"] = stats.Entries,
                ["hitRatio"] = stats.HitRatio,
                ["evictions"] = stats.Evictions,
                ["memoryUsageBytes"] = stats.MemoryUsage
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "MethodCache did not answer.", ex);
        }
    }
}
