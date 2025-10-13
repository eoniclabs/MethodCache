using MethodCache.Core.Options;

namespace MethodCache.Providers.Memory.Configuration;

/// <summary>
/// Configuration options for advanced memory cache provider.
/// </summary>
public class AdvancedMemoryOptions
{
    /// <summary>
    /// Maximum number of entries to store in memory.
    /// </summary>
    public long MaxEntries { get; set; } = 100000;

    /// <summary>
    /// Maximum memory usage in bytes (approximate).
    /// Default: 256MB - balanced for typical applications.
    /// </summary>
    public long MaxMemoryUsage { get; set; } = 256 * 1024 * 1024; // 256MB

    /// <summary>
    /// Eviction policy to use when limits are reached.
    /// </summary>
    public EvictionPolicy EvictionPolicy { get; set; } = EvictionPolicy.LRU;

    /// <summary>
    /// How often to run cleanup for expired entries.
    /// This is the base interval - actual cleanup frequency may be higher
    /// based on memory pressure. Under high memory pressure, cleanup
    /// can occur as frequently as every 30 seconds.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Minimum cleanup interval when under memory pressure.
    /// Default is 30 seconds to prevent excessive cleanup overhead.
    /// </summary>
    public TimeSpan MinCleanupInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Memory pressure threshold (0.0 to 1.0) at which to increase cleanup frequency.
    /// When memory usage exceeds this percentage of MaxMemoryUsage, cleanup will
    /// run more frequently. Default is 0.8 (80%).
    /// </summary>
    public double MemoryPressureThreshold { get; set; } = 0.8;

    /// <summary>
    /// Memory usage calculation mode.
    /// </summary>
    public MemoryUsageCalculationMode MemoryCalculationMode { get; set; } = MemoryUsageCalculationMode.Accurate;

    /// <summary>
    /// Maximum number of tag mappings to prevent unbounded growth.
    /// </summary>
    public int MaxTagMappings { get; set; } = 100000;

    /// <summary>
    /// Whether to enable detailed statistics tracking.
    /// </summary>
    public bool EnableDetailedStats { get; set; } = true;

    /// <summary>
    /// Whether to enable automatic cleanup of expired entries.
    /// </summary>
    public bool EnableAutomaticCleanup { get; set; } = true;

    /// <summary>
    /// Probability (0.0 to 1.0) of updating LRU access order on each cache hit.
    /// Default 1% (0.01) provides approximate LRU with 99% reduction in lock contention.
    /// Set to 1.0 for precise LRU semantics (every access updates order).
    /// This Redis-style probabilistic approach provides ~50% performance improvement with minimal accuracy loss.
    /// </summary>
    public double LruUpdateProbability { get; set; } = 0.01;
}

/// <summary>
/// Eviction policies for when memory limits are reached.
/// </summary>
public enum EvictionPolicy
{
    /// <summary>
    /// Least Recently Used - evict entries that haven't been accessed recently.
    /// </summary>
    LRU,

    /// <summary>
    /// Least Frequently Used - evict entries with lowest access count.
    /// </summary>
    LFU,

    /// <summary>
    /// Time To Live - evict entries that are closest to expiration.
    /// </summary>
    TTL,

    /// <summary>
    /// Random - evict random entries.
    /// </summary>
    Random
}

/// <summary>
/// Memory usage calculation modes.
/// </summary>
public enum MemoryUsageCalculationMode
{
    /// <summary>
    /// Use estimated calculation for better performance.
    /// </summary>
    Estimated,

    /// <summary>
    /// Use more accurate calculation with higher overhead.
    /// </summary>
    Accurate
}