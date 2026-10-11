namespace MethodCache.Redis;

/// <summary>
/// Options for using Redis as MethodCache's distributed cache.
/// </summary>
public class RedisOptions
{
    /// <summary>
    /// The StackExchange.Redis configuration string, for example <c>localhost:6379</c>.
    /// </summary>
    public string Configuration { get; set; } = "localhost:6379";

    /// <summary>
    /// Prefix for every key MethodCache writes, so several applications can share one Redis.
    /// </summary>
    public string KeyPrefix { get; set; } = "methodcache:";

    /// <summary>
    /// Whether to send invalidations over Redis pub/sub, so other instances drop their in-memory copies at once.
    /// Without it they keep them until they expire.
    /// </summary>
    public bool EnableBackplane { get; set; } = true;

    /// <summary>
    /// The pub/sub channel the backplane uses. Applications that share Redis but not data need different channels.
    /// </summary>
    public string BackplaneChannel { get; set; } = "methodcache:invalidations";
}
