using System;

namespace MethodCache.Core.Configuration
{
    /// <summary>
    /// Configuration options for the in-memory cache.
    /// </summary>
    public class MemoryCacheOptions
    {
        private long _maxItems = 10000;
        
        /// <summary>
        /// Maximum number of items in the cache.
        /// </summary>
        public long MaxItems 
        { 
            get => _maxItems;
            set
            {
                if (value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(MaxItems), value, "MaxItems must be greater than zero.");
                if (value > int.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(MaxItems), value, $"MaxItems cannot exceed {int.MaxValue}.");
                _maxItems = value;
            }
        }

        private long _maxMemoryBytes = 1000 * 1024 * 1024; // 1000MB
        
        /// <summary>
        /// Maximum memory size for the cache in bytes.
        /// </summary>
        public long MaxMemoryBytes 
        { 
            get => _maxMemoryBytes;
            set
            {
                if (value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(MaxMemoryBytes), value, "MaxMemoryBytes must be greater than zero.");
                _maxMemoryBytes = value;
            }
        }

        private TimeSpan _defaultExpiration = TimeSpan.FromMinutes(5);
        
        /// <summary>
        /// Default expiration time for cache entries.
        /// </summary>
        public TimeSpan DefaultExpiration 
        { 
            get => _defaultExpiration;
            set
            {
                if (value <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(DefaultExpiration), value, "DefaultExpiration must be greater than zero.");
                if (value > TimeSpan.FromDays(365))
                    throw new ArgumentOutOfRangeException(nameof(DefaultExpiration), value, "DefaultExpiration cannot exceed 365 days.");
                _defaultExpiration = value;
            }
        }

        private TimeSpan _maxExpiration = TimeSpan.FromDays(7);
        
        /// <summary>
        /// Maximum expiration time for cache entries.
        /// </summary>
        public TimeSpan MaxExpiration 
        { 
            get => _maxExpiration;
            set
            {
                if (value <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(MaxExpiration), value, "MaxExpiration must be greater than zero.");
                if (value > TimeSpan.FromDays(365))
                    throw new ArgumentOutOfRangeException(nameof(MaxExpiration), value, "MaxExpiration cannot exceed 365 days.");
                _maxExpiration = value;
            }
        }

        /// <summary>
        /// Eviction policy when the cache reaches capacity.
        /// </summary>
        public MemoryCacheEvictionPolicy EvictionPolicy { get; set; } = MemoryCacheEvictionPolicy.LRU;

        private TimeSpan _cleanupInterval = TimeSpan.FromMinutes(1);
        
        /// <summary>
        /// Interval for background cleanup of expired entries.
        /// </summary>
        public TimeSpan CleanupInterval 
        { 
            get => _cleanupInterval;
            set
            {
                if (value < TimeSpan.FromSeconds(10))
                    throw new ArgumentOutOfRangeException(nameof(CleanupInterval), value, "CleanupInterval must be at least 10 seconds.");
                if (value > TimeSpan.FromHours(24))
                    throw new ArgumentOutOfRangeException(nameof(CleanupInterval), value, "CleanupInterval cannot exceed 24 hours.");
                _cleanupInterval = value;
            }
        }

        /// <summary>
        /// Whether to enable background cleanup timer.
        /// </summary>
        public bool EnableBackgroundCleanup { get; set; } = true;

        /// <summary>
        /// Whether to enable detailed statistics tracking.
        /// </summary>
        public bool EnableStatistics { get; set; } = true;

        /// <summary>
        /// Whether to enable the ultra-fast cache hit path.
        /// When enabled, skips metrics tracking, LRU updates, and refresh-ahead checks on cache hits
        /// for zero-parameter methods returning reference types, providing 2-3x performance improvement.
        /// Cache misses always use the full-featured path with all protections.
        /// Default: true
        /// </summary>
        public bool EnableFastPath { get; set; } = true;

        /// <summary>
        /// When fast path is enabled, whether to still track metrics on cache hits.
        /// Enabling this adds a small overhead but maintains accurate hit/miss statistics.
        /// Default: false (prioritize performance)
        /// </summary>
        public bool FastPathTrackMetrics { get; set; } = false;

        /// <summary>
        /// Memory usage calculation mode.
        /// </summary>
        public MemoryUsageCalculationMode MemoryCalculationMode { get; set; } = MemoryUsageCalculationMode.Fast;

        private double _samplingPercentage = 0.1; // 10%
        
        /// <summary>
        /// Sample size for sampling mode (percentage of entries to measure).
        /// </summary>
        public double SamplingPercentage 
        { 
            get => _samplingPercentage;
            set
            {
                if (value <= 0 || value > 1)
                    throw new ArgumentOutOfRangeException(nameof(SamplingPercentage), value, "SamplingPercentage must be between 0 (exclusive) and 1 (inclusive).");
                _samplingPercentage = value;
            }
        }

        private int _accurateModeRecalculationInterval = 1000;
        
        /// <summary>
        /// How often to recalculate memory usage in accurate mode (in cache operations).
        /// </summary>
        public int AccurateModeRecalculationInterval 
        { 
            get => _accurateModeRecalculationInterval;
            set
            {
                if (value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(AccurateModeRecalculationInterval), value, "AccurateModeRecalculationInterval must be greater than zero.");
                _accurateModeRecalculationInterval = value;
            }
        }
        
        private double _evictionSamplePercentage = 0.1; // 10%

        /// <summary>
        /// Sample size percentage for approximate eviction policies (LFU, TTL).
        /// Default 10% provides good approximation with much better performance.
        /// Set to 100% to scan entire cache (equivalent to precise policies).
        /// </summary>
        public double EvictionSamplePercentage
        {
            get => _evictionSamplePercentage;
            set
            {
                if (value <= 0 || value > 1)
                    throw new ArgumentOutOfRangeException(nameof(EvictionSamplePercentage), value, "EvictionSamplePercentage must be between 0 (exclusive) and 1 (inclusive).");
                _evictionSamplePercentage = value;
            }
        }
    }

    /// <summary>
    /// Eviction policies for the memory cache.
    /// </summary>
    public enum MemoryCacheEvictionPolicy
    {
        /// <summary>
        /// Least Recently Used - evicts the least recently accessed items first.
        /// Uses lazy timestamp-based approach: updates timestamps on hits, sorts only at eviction time.
        /// </summary>
        LRU,

        /// <summary>
        /// Least Frequently Used - evicts the least frequently accessed items first.
        /// WARNING: Uses sampling approximation for performance. Not guaranteed to be globally optimal.
        /// See LFU_Precise for exact semantics with O(N) performance cost.
        /// </summary>
        LFU,

        /// <summary>
        /// Least Frequently Used (Precise) - evicts the globally least frequently used item.
        /// Guarantees precise LFU semantics but with O(N log N) performance cost on eviction.
        /// </summary>
        LFU_Precise,

        /// <summary>
        /// First In First Out - evicts the oldest items first.
        /// Uses creation timestamp sorting for precise FIFO semantics.
        /// </summary>
        FIFO,

        /// <summary>
        /// Time To Live - evicts items closest to expiration first.
        /// WARNING: Uses sampling approximation for performance. Not guaranteed to be globally optimal.
        /// See TTL_Precise for exact semantics with O(N) performance cost.
        /// </summary>
        TTL,
        
        /// <summary>
        /// Time To Live (Precise) - evicts the item globally closest to expiration.
        /// Guarantees precise TTL semantics but with O(N log N) performance cost on eviction.
        /// </summary>
        TTL_Precise
    }
}
