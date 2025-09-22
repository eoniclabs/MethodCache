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
    /// </summary>
    public long MaxMemoryUsage { get; set; } = 100 * 1024 * 1024; // 100MB

    /// <summary>
    /// Eviction policy to use when limits are reached.
    /// </summary>
    public EvictionPolicy EvictionPolicy { get; set; } = EvictionPolicy.LRU;

    /// <summary>
    /// How often to run cleanup for expired entries.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

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