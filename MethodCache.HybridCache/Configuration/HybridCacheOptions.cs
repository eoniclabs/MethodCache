using System;

namespace MethodCache.HybridCache.Configuration
{
    /// <summary>
    /// Configuration options for the hybrid cache.
    /// </summary>
    public class HybridCacheOptions
    {
        /// <summary>
        /// Strategy for coordinating L1 and L2 caches.
        /// </summary>
        public HybridStrategy Strategy { get; set; } = HybridStrategy.WriteThrough;

        /// <summary>
        /// Default expiration time for L1 cache entries.
        /// </summary>
        public TimeSpan L1DefaultExpiration { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Maximum expiration time for L1 cache entries.
        /// </summary>
        public TimeSpan L1MaxExpiration { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Default expiration time for L2 cache entries.
        /// </summary>
        public TimeSpan L2DefaultExpiration { get; set; } = TimeSpan.FromHours(4);

        /// <summary>
        /// Whether L2 cache is enabled.
        /// </summary>
        public bool L2Enabled { get; set; } = true;

        /// <summary>
        /// Maximum number of items in L1 cache.
        /// </summary>
        public long L1MaxItems { get; set; } = 10000;

        /// <summary>
        /// Maximum memory size for L1 cache in bytes.
        /// </summary>
        public long L1MaxMemoryBytes { get; set; } = 100 * 1024 * 1024; // 100MB

        /// <summary>
        /// Whether to warm L1 cache from L2 on startup.
        /// </summary>
        public bool EnableL1Warming { get; set; } = false;

        /// <summary>
        /// Whether to enable async writes to L2.
        /// </summary>
        public bool EnableAsyncL2Writes { get; set; } = true;

        /// <summary>
        /// Maximum concurrent L2 operations.
        /// </summary>
        public int MaxConcurrentL2Operations { get; set; } = 10;

        /// <summary>
        /// L1 cache eviction policy.
        /// </summary>
        public L1EvictionPolicy L1EvictionPolicy { get; set; } = L1EvictionPolicy.LRU;

        /// <summary>
        /// Whether to use the backplane for cache invalidation.
        /// </summary>
        public bool EnableBackplane { get; set; } = true;

        /// <summary>
        /// Instance ID for this cache instance (used in backplane communication).
        /// </summary>
        public string InstanceId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Whether to log detailed debug information.
        /// </summary>
        public bool EnableDebugLogging { get; set; } = false;

        /// <summary>
        /// Retry policy for L2 operations.
        /// </summary>
        public RetryOptions L2RetryPolicy { get; set; } = new RetryOptions();
    }

    /// <summary>
    /// Strategy for coordinating L1 and L2 caches.
    /// </summary>
    public enum HybridStrategy
    {
        /// <summary>
        /// Write to both L1 and L2 synchronously.
        /// </summary>
        WriteThrough,

        /// <summary>
        /// Write to L1 immediately, write to L2 asynchronously.
        /// </summary>
        WriteBack,

        /// <summary>
        /// Only use L1 cache.
        /// </summary>
        L1Only,

        /// <summary>
        /// Only use L2 cache.
        /// </summary>
        L2Only,

        /// <summary>
        /// Read from L1, fallback to L2, write to both.
        /// </summary>
        ReadThrough
    }

    /// <summary>
    /// L1 cache eviction policies.
    /// </summary>
    public enum L1EvictionPolicy
    {
        /// <summary>
        /// Least Recently Used.
        /// </summary>
        LRU,

        /// <summary>
        /// Least Frequently Used.
        /// </summary>
        LFU,

        /// <summary>
        /// First In First Out.
        /// </summary>
        FIFO,

        /// <summary>
        /// Time To Live based.
        /// </summary>
        TTL
    }

    /// <summary>
    /// Retry options for L2 operations.
    /// </summary>
    public class RetryOptions
    {
        /// <summary>
        /// Maximum number of retry attempts.
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Base delay between retries.
        /// </summary>
        public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// Maximum delay between retries.
        /// </summary>
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Whether to use exponential backoff.
        /// </summary>
        public bool UseExponentialBackoff { get; set; } = true;
    }
}