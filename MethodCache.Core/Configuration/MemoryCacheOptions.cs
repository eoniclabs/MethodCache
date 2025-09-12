using System;

namespace MethodCache.Core.Configuration
{
    /// <summary>
    /// Configuration options for the in-memory cache.
    /// </summary>
    public class MemoryCacheOptions
    {
        /// <summary>
        /// Maximum number of items in the cache.
        /// </summary>
        public long MaxItems { get; set; } = 10000;

        /// <summary>
        /// Maximum memory size for the cache in bytes.
        /// </summary>
        public long MaxMemoryBytes { get; set; } = 1000 * 1024 * 1024; // 1000MB

        /// <summary>
        /// Default expiration time for cache entries.
        /// </summary>
        public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Maximum expiration time for cache entries.
        /// </summary>
        public TimeSpan MaxExpiration { get; set; } = TimeSpan.FromDays(7);

        /// <summary>
        /// Eviction policy when the cache reaches capacity.
        /// </summary>
        public MemoryCacheEvictionPolicy EvictionPolicy { get; set; } = MemoryCacheEvictionPolicy.LRU;

        /// <summary>
        /// Interval for background cleanup of expired entries.
        /// </summary>
        public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Whether to enable background cleanup timer.
        /// </summary>
        public bool EnableBackgroundCleanup { get; set; } = true;

        /// <summary>
        /// Whether to enable detailed statistics tracking.
        /// </summary>
        public bool EnableStatistics { get; set; } = true;

        /// <summary>
        /// Memory usage calculation mode.
        /// </summary>
        public MemoryUsageCalculationMode MemoryCalculationMode { get; set; } = MemoryUsageCalculationMode.Fast;

        /// <summary>
        /// Sample size for sampling mode (percentage of entries to measure).
        /// </summary>
        public double SamplingPercentage { get; set; } = 0.1; // 10%

        /// <summary>
        /// How often to recalculate memory usage in accurate mode (in cache operations).
        /// </summary>
        public int AccurateModeRecalculationInterval { get; set; } = 1000;
    }

    /// <summary>
    /// Eviction policies for the memory cache.
    /// </summary>
    public enum MemoryCacheEvictionPolicy
    {
        /// <summary>
        /// Least Recently Used - evicts the least recently accessed items first.
        /// </summary>
        LRU,

        /// <summary>
        /// Least Frequently Used - evicts the least frequently accessed items first.
        /// </summary>
        LFU,

        /// <summary>
        /// First In First Out - evicts the oldest items first.
        /// </summary>
        FIFO,

        /// <summary>
        /// Time To Live - evicts items closest to expiration first.
        /// </summary>
        TTL
    }
}
