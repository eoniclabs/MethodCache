using System;
using System.Collections.Generic;

namespace MethodCache.Providers.Redis.MultiRegion
{
    public class RegionConfiguration
    {
        public string Name { get; set; } = string.Empty;
        public string ConnectionString { get; set; } = string.Empty;
        public int Priority { get; set; } = 0;
        public bool IsPrimary { get; set; } = false;
        public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromMinutes(1);
        public TimeSpan MaxLatency { get; set; } = TimeSpan.FromMilliseconds(500);
        public RegionReplicationStrategy ReplicationStrategy { get; set; } = RegionReplicationStrategy.Eventually;
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    public class MultiRegionOptions
    {
        public List<RegionConfiguration> Regions { get; set; } = new();
        public string PrimaryRegion { get; set; } = string.Empty;
        public RegionFailoverStrategy FailoverStrategy { get; set; } = RegionFailoverStrategy.PriorityBased;
        public TimeSpan CrossRegionSyncInterval { get; set; } = TimeSpan.FromMinutes(5);
        public int MaxConcurrentSyncs { get; set; } = 3;
        public bool EnableCrossRegionInvalidation { get; set; } = true;
        public bool EnableRegionAffinity { get; set; } = true;
        public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(10);
        public int MaxRetryAttempts { get; set; } = 3;
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    }

    public enum RegionReplicationStrategy
    {
        None,           // No replication
        Eventually,     // Eventually consistent across regions
        Immediate,      // Immediate replication (sync)
        Selective       // Replicate only specific keys/patterns
    }

    public enum RegionFailoverStrategy
    {
        PriorityBased,  // Use regions based on priority
        LatencyBased,   // Use region with lowest latency
        RoundRobin,     // Distribute load across regions
        Sticky          // Stick to one region until it fails
    }
}