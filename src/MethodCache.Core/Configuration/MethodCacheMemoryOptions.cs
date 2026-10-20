using System;

namespace MethodCache
{
    /// <summary>
    /// Configuration options for the in-memory cache.
    /// </summary>
    public sealed class MethodCacheMemoryOptions
    {
        private long _maxItems = 10000;

        /// <summary>
        /// The most method results kept in memory. Default 10 000. Always applies, also together with
        /// <see cref="WeightLimit"/>.
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

        private long _weightLimit = 1000 * 1024 * 1024; // 1000MB
        private bool _weightLimitSet;

        /// <summary>
        /// The most total weight kept, in the units <see cref="Weigher"/> returns (typically bytes). Default 1000 MB.
        /// Applies together with <see cref="MaxItems"/>, so whichever limit is reached first evicts. Setting it requires
        /// a <see cref="Weigher"/>, since object sizes cannot be measured reliably at runtime: without one the cache
        /// fails at startup instead of ignoring the limit.
        /// </summary>
        public long WeightLimit
        {
            get => _weightLimit;
            set
            {
                if (value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(WeightLimit), value, "WeightLimit must be greater than zero.");
                _weightLimit = value;
                _weightLimitSet = true;
            }
        }
        private long _maxHttpBytes = 100 * 1024 * 1024;
        /// <summary>Maximum resident HTTP storage cost in bytes, including bodies, headers and conservative metadata overhead.
        /// Shared by all HTTP clients; independent of method weights. Default 100 MiB.</summary>
        public long MaxHttpBytes
        {
            get => _maxHttpBytes;
            set { if (value <= 0) throw new ArgumentOutOfRangeException(nameof(MaxHttpBytes)); _maxHttpBytes = value; }
        }

        private TimeSpan _defaultExpiration = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Default expiration time for cache entries.
        /// </summary>
        internal TimeSpan DefaultExpiration
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
        internal MemoryCacheEvictionPolicy EvictionPolicy { get; set; } = MemoryCacheEvictionPolicy.TinyLfu;

        /// <summary>
        /// Optional function returning the weight of an entry (for example its size in bytes). When set, the cache is
        /// bounded by <see cref="WeightLimit"/> measured in these units as well as by <see cref="MaxItems"/>.
        /// Must be fast, thread-safe and return a non-negative value.
        /// </summary>
        public Func<string, object?, long>? Weigher { get; set; }

        /// <summary>Throws when the options contradict each other, so the cache fails at startup instead of on use.</summary>
        internal void EnsureValid()
        {
            if (_weightLimitSet && Weigher == null)
                throw new InvalidOperationException(
                    "MethodCacheMemoryOptions.WeightLimit needs a Weigher to measure entries. Set Weigher too, or bound the cache by MaxItems only.");
        }

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
        internal TimeSpan CleanupInterval
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
        internal bool EnableBackgroundCleanup { get; set; } = true;

        /// <summary>
        /// Whether to count hits, misses and evictions, including hits on the fast path. Each count is a striped
        /// counter increment; turn it off only if you never read the statistics.
        /// </summary>
        internal bool EnableStatistics { get; set; } = true;

        /// <summary>
        /// Whether generated decorators answer cache hits from a lock-free lookup that allocates nothing. Expiration,
        /// sliding expiration and refresh-ahead still apply; misses always take the full path. Default true; turning
        /// it off only makes hits slower.
        /// </summary>
        internal bool EnableFastPath { get; set; } = true;

    }

    /// <summary>
    /// Eviction policies for the memory cache.
    /// </summary>
    internal enum MemoryCacheEvictionPolicy
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
