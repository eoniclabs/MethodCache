using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Hybrid
{
    public class HybridCacheHealthCheck : IHealthCheck
    {
        private readonly IHybridCacheManager _hybridCacheManager;
        private readonly IL1Cache _l1Cache;
        private readonly ILogger<HybridCacheHealthCheck> _logger;

        public HybridCacheHealthCheck(
            IHybridCacheManager hybridCacheManager,
            IL1Cache l1Cache,
            ILogger<HybridCacheHealthCheck> logger)
        {
            _hybridCacheManager = hybridCacheManager;
            _l1Cache = l1Cache;
            _logger = logger;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                var data = new Dictionary<string, object>();

                // Test L1 Cache
                var l1Healthy = await CheckL1HealthAsync(data);
                
                // Test L2 Cache
                var l2Healthy = await CheckL2HealthAsync(data);

                // Get hybrid cache statistics
                await AddHybridStatsAsync(data);

                // Overall health assessment
                if (!l1Healthy && !l2Healthy)
                {
                    return HealthCheckResult.Unhealthy("Both L1 and L2 caches are unhealthy", null, data);
                }
                else if (!l1Healthy)
                {
                    return HealthCheckResult.Degraded("L1 cache is unhealthy, operating on L2 only", null, data);
                }
                else if (!l2Healthy)
                {
                    return HealthCheckResult.Degraded("L2 cache is unhealthy, operating on L1 only", null, data);
                }
                else
                {
                    return HealthCheckResult.Healthy("Hybrid cache is healthy", data);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hybrid cache health check failed");
                return HealthCheckResult.Unhealthy("Hybrid cache health check failed", ex);
            }
        }

        private async Task<bool> CheckL1HealthAsync(Dictionary<string, object> data)
        {
            try
            {
                // Test L1 basic operations
                var testKey = $"health_check_l1_{Guid.NewGuid():N}";
                var testValue = $"test_value_{DateTimeOffset.UtcNow:O}";

                // Test SET
                await _l1Cache.SetAsync(testKey, testValue, TimeSpan.FromMinutes(1));
                data.Add("l1_set_success", true);

                // Test GET
                var retrievedValue = await _l1Cache.GetAsync<string>(testKey);
                var getSuccess = retrievedValue == testValue;
                data.Add("l1_get_success", getSuccess);

                // Test EXISTS
                var existsResult = await _l1Cache.ExistsAsync(testKey);
                data.Add("l1_exists_success", existsResult);

                // Test REMOVE
                var removeSuccess = await _l1Cache.RemoveAsync(testKey);
                data.Add("l1_remove_success", removeSuccess);

                // Get L1 statistics
                var l1Stats = await _l1Cache.GetStatsAsync();
                data.Add("l1_entries", l1Stats.Entries);
                data.Add("l1_hits", l1Stats.Hits);
                data.Add("l1_misses", l1Stats.Misses);
                data.Add("l1_hit_ratio", l1Stats.HitRatio);
                data.Add("l1_evictions", l1Stats.Evictions);
                data.Add("l1_memory_usage_bytes", l1Stats.MemoryUsageBytes);

                data.Add("l1_status", "healthy");
                return getSuccess && existsResult && removeSuccess;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "L1 cache health check failed");
                data.Add("l1_status", "unhealthy");
                data.Add("l1_error", ex.Message);
                return false;
            }
        }

        private async Task<bool> CheckL2HealthAsync(Dictionary<string, object> data)
        {
            try
            {
                // Test L2 basic operations
                var testKey = $"health_check_l2_{Guid.NewGuid():N}";
                var testValue = $"test_value_{DateTimeOffset.UtcNow:O}";

                // Test SET
                await _hybridCacheManager.SetInL2Async(testKey, testValue, TimeSpan.FromMinutes(1));
                data.Add("l2_set_success", true);

                // Test GET
                var retrievedValue = await _hybridCacheManager.GetFromL2Async<string>(testKey);
                var getSuccess = retrievedValue == testValue;
                data.Add("l2_get_success", getSuccess);

                // Test INVALIDATE
                await _hybridCacheManager.InvalidateL2Async(testKey);
                data.Add("l2_invalidate_success", true);

                data.Add("l2_status", "healthy");
                return getSuccess;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "L2 cache health check failed");
                data.Add("l2_status", "unhealthy");
                data.Add("l2_error", ex.Message);
                return false;
            }
        }

        private async Task AddHybridStatsAsync(Dictionary<string, object> data)
        {
            try
            {
                var hybridStats = await _hybridCacheManager.GetStatsAsync();
                
                data.Add("hybrid_l1_hits", hybridStats.L1Hits);
                data.Add("hybrid_l1_misses", hybridStats.L1Misses);
                data.Add("hybrid_l2_hits", hybridStats.L2Hits);
                data.Add("hybrid_l2_misses", hybridStats.L2Misses);
                data.Add("hybrid_l1_evictions", hybridStats.L1Evictions);
                data.Add("hybrid_l1_entries", hybridStats.L1Entries);
                data.Add("hybrid_l2_entries", hybridStats.L2Entries);
                data.Add("hybrid_l1_hit_ratio", hybridStats.L1HitRatio);
                data.Add("hybrid_l2_hit_ratio", hybridStats.L2HitRatio);
                data.Add("hybrid_overall_hit_ratio", hybridStats.OverallHitRatio);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get hybrid cache statistics");
                data.Add("hybrid_stats_error", ex.Message);
            }
        }
    }
}