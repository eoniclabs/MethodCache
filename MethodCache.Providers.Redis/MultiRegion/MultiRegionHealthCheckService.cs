using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.MultiRegion
{
    public class MultiRegionHealthCheckService : IHealthCheck
    {
        private readonly IMultiRegionCacheManager _multiRegionManager;
        private readonly ILogger<MultiRegionHealthCheckService> _logger;

        public MultiRegionHealthCheckService(
            IMultiRegionCacheManager multiRegionManager,
            ILogger<MultiRegionHealthCheckService> logger)
        {
            _multiRegionManager = multiRegionManager;
            _logger = logger;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                var data = new Dictionary<string, object>();
                var availableRegions = await _multiRegionManager.GetAvailableRegionsAsync();
                var regionCount = availableRegions.Count();

                data.Add("total_regions", regionCount);

                if (regionCount == 0)
                {
                    return HealthCheckResult.Unhealthy("No regions available", null, data);
                }

                var healthyRegions = new List<string>();
                var unhealthyRegions = new List<string>();
                var totalLatency = TimeSpan.Zero;

                foreach (var region in availableRegions)
                {
                    try
                    {
                        var regionHealth = await _multiRegionManager.GetRegionHealthAsync(region);
                        
                        if (regionHealth.IsHealthy)
                        {
                            healthyRegions.Add(region);
                            totalLatency = totalLatency.Add(regionHealth.Latency);
                            data.Add($"region_{region}_status", "healthy");
                            data.Add($"region_{region}_latency_ms", regionHealth.Latency.TotalMilliseconds);
                            
                            // Add region-specific metrics
                            foreach (var metric in regionHealth.Metrics)
                            {
                                data.Add($"region_{region}_{metric.Key}", metric.Value);
                            }
                        }
                        else
                        {
                            unhealthyRegions.Add(region);
                            data.Add($"region_{region}_status", "unhealthy");
                            data.Add($"region_{region}_error", regionHealth.ErrorMessage ?? "Unknown error");
                        }
                    }
                    catch (Exception ex)
                    {
                        unhealthyRegions.Add(region);
                        data.Add($"region_{region}_status", "error");
                        data.Add($"region_{region}_error", ex.Message);
                        _logger.LogWarning(ex, "Failed to check health for region {Region}", region);
                    }
                }

                data.Add("healthy_regions", healthyRegions.Count);
                data.Add("unhealthy_regions", unhealthyRegions.Count);
                data.Add("healthy_region_list", string.Join(", ", healthyRegions));
                
                if (unhealthyRegions.Any())
                {
                    data.Add("unhealthy_region_list", string.Join(", ", unhealthyRegions));
                }

                if (healthyRegions.Any())
                {
                    var averageLatency = totalLatency.TotalMilliseconds / healthyRegions.Count;
                    data.Add("average_latency_ms", averageLatency);
                }

                // Determine overall health
                if (healthyRegions.Count == 0)
                {
                    return HealthCheckResult.Unhealthy("All regions are unhealthy", null, data);
                }
                else if (unhealthyRegions.Count > 0)
                {
                    var message = $"{unhealthyRegions.Count} of {regionCount} regions are unhealthy";
                    return HealthCheckResult.Degraded(message, null, data);
                }
                else
                {
                    return HealthCheckResult.Healthy("All regions are healthy", data);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Multi-region health check failed");
                return HealthCheckResult.Unhealthy("Multi-region health check failed", ex);
            }
        }
    }
}