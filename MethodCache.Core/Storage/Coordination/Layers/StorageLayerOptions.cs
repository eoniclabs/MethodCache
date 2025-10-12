namespace MethodCache.Core.Storage.Coordination.Layers;

/// <summary>
/// Configuration options for storage layers.
/// </summary>
public sealed class StorageLayerOptions
{
    /// <summary>
    /// Gets or sets the maximum expiration time for L1 (memory) cache entries.
    /// Default: 15 minutes.
    /// </summary>
    public TimeSpan L1MaxExpiration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Gets or sets the default expiration time for L2 (distributed) cache entries.
    /// Default: 60 minutes.
    /// </summary>
    public TimeSpan L2DefaultExpiration { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Gets or sets the default expiration time for L3 (persistent) cache entries.
    /// Default: 24 hours.
    /// </summary>
    public TimeSpan L3DefaultExpiration { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Gets or sets the maximum expiration time for L3 (persistent) cache entries.
    /// Default: 7 days.
    /// </summary>
    public TimeSpan L3MaxExpiration { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Gets or sets whether the L2 layer is enabled.
    /// Default: true.
    /// </summary>
    public bool L2Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the L3 layer is enabled.
    /// Default: true.
    /// </summary>
    public bool L3Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether async writes to L2 are enabled.
    /// Default: false (synchronous writes).
    /// </summary>
    public bool EnableAsyncL2Writes { get; set; } = false;

    /// <summary>
    /// Gets or sets whether async writes to L3 are enabled.
    /// Default: false (synchronous writes).
    /// </summary>
    public bool EnableAsyncL3Writes { get; set; } = false;

    /// <summary>
    /// Gets or sets the capacity of the async write queue.
    /// Default: 1000.
    /// </summary>
    public int AsyncWriteQueueCapacity { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the maximum number of concurrent L2 operations.
    /// Default: 10.
    /// </summary>
    public int MaxConcurrentL2Operations { get; set; } = 10;

    /// <summary>
    /// Gets or sets the maximum number of concurrent L3 operations.
    /// Default: 5.
    /// </summary>
    public int MaxConcurrentL3Operations { get; set; } = 5;

    /// <summary>
    /// Gets or sets whether L3 promotion is enabled (promote L3 hits to L1/L2).
    /// Default: true.
    /// </summary>
    public bool EnableL3Promotion { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the backplane is enabled for cross-instance invalidation.
    /// Default: true.
    /// </summary>
    public bool EnableBackplane { get; set; } = true;

    /// <summary>
    /// Gets or sets the instance ID for backplane coordination.
    /// Default: random GUID.
    /// </summary>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString();
}
