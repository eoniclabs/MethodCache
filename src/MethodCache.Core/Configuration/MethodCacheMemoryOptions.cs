using System;

namespace MethodCache
{
    /// <summary>
    /// Configuration options for the in-memory cache.
    /// </summary>
    public class MethodCacheMemoryOptions
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
        /// Whether generated decorators answer cache hits from a lock-free lookup that allocates nothing. Expiration,
        /// sliding expiration and refresh-ahead still apply; misses always take the full path. Default true; turning
        /// it off only makes hits slower.
        /// </summary>
        public bool EnableFastPath { get; set; } = true;

        /// <summary>
        /// Memory usage calculation mode.
        /// </summary>
        public MemoryUsageCalculationMode MemoryCalculationMode { get; set; } = MemoryUsageCalculationMode.Fast;

        private double _samplingPercentage = 0.1; // 10%

        /// <summary>
        /// Target sample size for sampling mode (fraction of entries), bounded by <see cref="SamplingMaxEntries"/>.
        /// </summary>
        public double SamplingPercentage
        {
            get => _samplingPercentage;
            set
            {
                if (!double.IsFinite(value) || value <= 0 || value > 1)
                    throw new ArgumentOutOfRangeException(nameof(SamplingPercentage), value, "SamplingPercentage must be between 0 (exclusive) and 1 (inclusive).");
                _samplingPercentage = value;
            }
        }

        private int _samplingMaxEntries = 1024;

        /// <summary>Maximum entries serialized by one sampling estimate. Default 1024; must be positive.</summary>
        public int SamplingMaxEntries
        {
            get => _samplingMaxEntries;
            set
            {
                if (value <= 0) throw new ArgumentOutOfRangeException(nameof(SamplingMaxEntries), value, "SamplingMaxEntries must be positive.");
                _samplingMaxEntries = value;
            }
        }

        private int _samplingRecalculationInterval = 100;

        /// <summary>
        /// Statistics reads between new samples. Default 100; 1 resamples every read. Between samples the previous
        /// average entry estimate is scaled by the current entry count. Must be positive.
        /// </summary>
        public int SamplingRecalculationInterval
        {
            get => _samplingRecalculationInterval;
            set
            {
                if (value <= 0) throw new ArgumentOutOfRangeException(nameof(SamplingRecalculationInterval), value, "SamplingRecalculationInterval must be positive.");
                _samplingRecalculationInterval = value;
            }
        }

        private int _accurateModeRecalculationInterval = 1000;

        /// <summary>
        /// How often to recalculate memory usage in accurate mode (in statistics reads).
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
    }

    /// <summary>
    /// Eviction policies for the memory cache.
    /// </summary>
    public enum MemoryCacheEvictionPolicy
    {
        /// <summary>
        /// W-TinyLFU (the default): a small LRU admission window in front of a segmented LRU, where a
        /// frequency sketch decides whether a new entry may displace an existing one. Gives near-optimal
        /// hit rates, resists one-off scans and evicts in O(1). Same algorithm as Caffeine.
        /// </summary>
        TinyLfu,

        /// <summary>
        /// Least recently used: evicts the least recently accessed items first.
        /// </summary>
        Lru,

        /// <summary>
        /// First in, first out: evicts the oldest items first; reads do not change the order.
        /// </summary>
        Fifo
    }
}
