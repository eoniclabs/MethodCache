using StackExchange.Redis;

namespace MethodCache.Redis;

/// <summary>
/// Options for using Redis as MethodCache's distributed cache.
/// </summary>
public class RedisOptions
{
    /// <summary>
    /// The StackExchange.Redis configuration string, for example <c>localhost:6379</c>. MethodCache opens its own
    /// connection with it, unless <see cref="ConnectionMultiplexerFactory"/> is set.
    /// </summary>
    public string Configuration { get; set; } = "localhost:6379";

    /// <summary>
    /// Returns a connection your app already has, for MethodCache to share instead of opening its own, for example
    /// <c>sp =&gt; sp.GetRequiredService&lt;IConnectionMultiplexer&gt;()</c>. MethodCache doesn't dispose it.
    /// </summary>
    public Func<IServiceProvider, IConnectionMultiplexer>? ConnectionMultiplexerFactory { get; set; }

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
    /// The pub/sub channel the backplane uses. Defaults to <see cref="KeyPrefix"/> followed by
    /// <c>invalidations</c>, so applications with different prefixes never clear each other's memory.
    /// </summary>
    public string? BackplaneChannel { get; set; }

    internal string EffectiveBackplaneChannel => string.IsNullOrEmpty(BackplaneChannel) ? KeyPrefix + "invalidations" : BackplaneChannel;
}
