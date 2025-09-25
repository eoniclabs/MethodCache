namespace MethodCache.Core.Configuration;

/// <summary>
/// Base configuration options for storage providers.
/// </summary>
public class StorageOptions
{
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
    /// Maximum expiration time for L2 cache entries.
    /// </summary>
    public TimeSpan L2MaxExpiration { get; set; } = TimeSpan.FromHours(24);

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
    /// Maximum number of pending asynchronous write operations queued for background processing.
    /// </summary>
    public int AsyncWriteQueueCapacity { get; set; } = 1024;

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

    /// <summary>
    /// Whether to enable efficient tag-based L1 invalidation.
    /// When enabled, tracks tag-to-key mappings for surgical invalidations.
    /// When disabled, falls back to clearing entire L1 cache on tag invalidation.
    /// </summary>
    public bool EnableEfficientL1TagInvalidation { get; set; } = true;

    /// <summary>
    /// Maximum number of tag-to-key mappings to keep in memory.
    /// Prevents memory bloat from excessive tag tracking.
    /// </summary>
    public int MaxTagMappings { get; set; } = 50000;

    /// <summary>
    /// Key prefix to use for all cache operations.
    /// </summary>
    public string KeyPrefix { get; set; } = "cache:";

    // L3 Persistent Storage Configuration

    /// <summary>
    /// Default expiration time for L3 persistent cache entries.
    /// </summary>
    public TimeSpan L3DefaultExpiration { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Maximum expiration time for L3 persistent cache entries.
    /// </summary>
    public TimeSpan L3MaxExpiration { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Whether L3 persistent cache is enabled.
    /// </summary>
    public bool L3Enabled { get; set; } = false;

    /// <summary>
    /// Maximum concurrent L3 operations.
    /// </summary>
    public int MaxConcurrentL3Operations { get; set; } = 5;

    /// <summary>
    /// Whether to enable automatic cleanup of expired L3 entries.
    /// </summary>
    public bool EnableL3Cleanup { get; set; } = true;

    /// <summary>
    /// Interval for L3 cleanup operations.
    /// </summary>
    public TimeSpan L3CleanupInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Whether to enable async writes to L3.
    /// </summary>
    public bool EnableAsyncL3Writes { get; set; } = true;

    /// <summary>
    /// Whether to promote cache hits from L3 to L1/L2.
    /// </summary>
    public bool EnableL3Promotion { get; set; } = true;

    /// <summary>
    /// Minimum time a key must be accessed before promotion from L3.
    /// Prevents promoting rarely accessed items.
    /// </summary>
    public TimeSpan L3PromotionThreshold { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Maximum size limit for L3 storage in bytes (0 = unlimited).
    /// </summary>
    public long L3MaxStorageSizeBytes { get; set; } = 0;

    /// <summary>
    /// Retry policy for L3 operations.
    /// </summary>
    public RetryOptions L3RetryPolicy { get; set; } = new RetryOptions
    {
        MaxRetries = 2,
        BaseDelay = TimeSpan.FromMilliseconds(500),
        MaxDelay = TimeSpan.FromSeconds(10)
    };
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
