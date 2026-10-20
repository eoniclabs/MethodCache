using StackExchange.Redis;

namespace MethodCache.Redis;

/// <summary>
/// Options for using Redis as MethodCache's distributed cache.
/// </summary>
public sealed class RedisOptions
{
    /// <summary>
    /// The StackExchange.Redis configuration string, for example <c>localhost:6379</c>. MethodCache opens its own
    /// connection with it, unless <see cref="ConnectionMultiplexerFactory"/> is set. There is no default: startup
    /// fails when neither is set, so a missing configuration section never makes MethodCache connect to localhost.
    /// </summary>
    public string? Configuration { get; set; }

    /// <summary>
    /// Returns a connection your app already has, for MethodCache to share instead of opening its own, for example
    /// <c>sp =&gt; sp.GetRequiredService&lt;IConnectionMultiplexer&gt;()</c>. MethodCache doesn't dispose it.
    /// </summary>
    public Func<IServiceProvider, IConnectionMultiplexer>? ConnectionMultiplexerFactory { get; set; }

    /// <summary>
    /// Whether to send invalidations over Redis pub/sub, so other instances drop their in-memory copies at once.
    /// Without it they keep them until they expire.
    /// </summary>
    public bool EnableBackplane { get; set; } = true;
}
