using MethodCache.AspNetCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the MethodCache health check.</summary>
public static class MethodCacheHealthChecksBuilderExtensions
{
    /// <summary>Adds a health check that reports the cache's availability and statistics.</summary>
    public static IHealthChecksBuilder AddMethodCache(
        this IHealthChecksBuilder builder,
        string name = "methodcache",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCheck<MethodCacheHealthCheck>(name, failureStatus ?? HealthStatus.Degraded, tags ?? Array.Empty<string>());
    }
}
