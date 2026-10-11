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
        /// Maximum total weight of the cache when a <see cref="Weigher"/> is configured (typically bytes).
        /// Ignored without a weigher, since object sizes cannot be measured reliably at runtime.
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
        /// Eviction policy when the cache reaches capacity. Defaults to <see cref="MemoryCacheEvictionPolicy.TinyLfu"/>.
        /// </summary>
        public MemoryCacheEvictionPolicy EvictionPolicy { get; set; } = MemoryCacheEvictionPolicy.TinyLfu;

        /// <summary>
        /// Optional function returning the cost of an entry (for example its size in bytes). When set, the
        /// cache is bounded by <see cref="MaxMemoryBytes"/> measured in these units instead of by <see cref="MaxItems"/>.
        /// Must be fast, thread-safe and return a non-negative value.
        /// </summary>
        public Func<string, object?, long>? Weigher { get; set; }

        /// <summary>
        /// When true, <c>null</c> results are cached like any other value, so a method returning null is not
        /// re-executed on every call (as Microsoft's HybridCache and FusionCache do). Default true; set it to false
        /// when a null result means "not there yet" and the next call should try again.
        /// </summary>
        public bool CacheNullValues { get; set; } = true;

        private TimeSpan _jitterMaxDuration = TimeSpan.Zero;

        /// <summary>
        /// Adds a random extra lifetime in [0, value) to each entry so entries created together do not all
        /// expire at the same moment. Default zero (disabled).
        /// </summary>
        public TimeSpan JitterMaxDuration
        {
            get => _jitterMaxDuration;
            set
            {
                if (value < TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(JitterMaxDuration), value, "JitterMaxDuration cannot be negative.");
                _jitterMaxDuration = value;
            }
        }

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
        /// Whether to count hits, misses and evictions, including hits on the fast path. Each count is a striped
        /// counter increment; turn it off only if you never read the statistics.
        /// </summary>
        public bool EnableStatistics { get; set; } = true;

        /// <summary>
        /// Whether to enable the ultra-fast cache hit path.
        /// When enabled, skips LRU updates and refresh-ahead checks on cache hits
        /// for zero-parameter methods returning reference types, providing 2-3x performance improvement.
        /// Cache misses always use the full-featured path with all protections.
        /// Default: true
        /// </summary>
        public bool EnableFastPath { get; set; } = true;

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
        /// No longer used: eviction is O(1) and does not sample.
        /// </summary>
        [Obsolete("Eviction no longer samples; this setting has no effect.")]
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
        /// </summary>
        LRU,

        /// <summary>
        /// Superseded by <see cref="TinyLfu"/>, which tracks frequency with far better hit rates and O(1) eviction.
        /// </summary>
        [Obsolete("Use TinyLfu. This value now selects TinyLfu.")]
        LFU,

        /// <summary>
        /// Superseded by <see cref="TinyLfu"/>.
        /// </summary>
        [Obsolete("Use TinyLfu. This value now selects TinyLfu.")]
        LFU_Precise,

        /// <summary>
        /// First In First Out - evicts the oldest items first; reads do not change the order.
        /// </summary>
        FIFO,

        /// <summary>
        /// Superseded by <see cref="TinyLfu"/>. Expired entries are always removed first by the timer wheel.
        /// </summary>
        [Obsolete("Use TinyLfu. This value now selects TinyLfu.")]
        TTL,

        /// <summary>
        /// Superseded by <see cref="TinyLfu"/>.
        /// </summary>
        [Obsolete("Use TinyLfu. This value now selects TinyLfu.")]
        TTL_Precise,

        /// <summary>
        /// W-TinyLFU (the default): a small LRU admission window in front of a segmented LRU, where a
        /// frequency sketch decides whether a new entry may displace an existing one. Gives near-optimal
        /// hit rates, resists one-off scans and evicts in O(1). Same algorithm as Caffeine.
        /// </summary>
        TinyLfu
    }
}
