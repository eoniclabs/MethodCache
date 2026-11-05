using MethodCache.Core.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Testing;

/// <summary>
/// Records what the cache of one container did: how many times each cached call ran your implementation, and the
/// keys and tags it invalidated. <c>AddMethodCacheTesting()</c> registers it; resolve it from the provider.
/// </summary>
/// <remarks>
/// It sees the built-in cache manager only: with your own <see cref="ICacheManager"/> or <see cref="NoOpCacheManager"/>
/// registered, it records nothing. Invalidations that arrive from other instances through a distributed cache are not
/// recorded, only the ones this container makes.
/// </remarks>
/// <example>
/// <code>
/// await products.GetAsync(42);
/// await products.GetAsync(42);
/// Assert.Equal(1, recorder.Loads&lt;IProductService&gt;(p =&gt; p.GetAsync(42)));   // the second call was a hit
///
/// await products.UpdatePriceAsync(42, 10m);
/// Assert.Contains("product:42", recorder.InvalidatedTags);
/// </code>
/// </example>
public sealed class CacheRecorder
{
    private readonly IServiceProvider _provider;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _loads = new(StringComparer.Ordinal);
    private readonly List<string> _tags = [];
    private readonly List<string> _keys = [];

    internal CacheRecorder(IServiceProvider provider)
    {
        _provider = provider;
        Sink = new EventSink(this);
    }

    internal ICacheEventSink Sink { get; }

    /// <summary>
    /// How many times <paramref name="call"/> ran your implementation: its misses, refreshes and reloads after an
    /// invalidation, since the container was built or <see cref="Clear"/> was called. Calls with other arguments count
    /// separately. The call runs on a stand-in, so the method itself doesn't run.
    /// </summary>
    /// <param name="call">Calls one cached method of <typeparamref name="TService"/>, such as <c>p =&gt; p.GetAsync(42)</c>.</param>
    /// <param name="serviceKey">The service key of a keyed registration, or <c>null</c> for the unkeyed one.</param>
    /// <exception cref="InvalidOperationException"><paramref name="call"/> doesn't call exactly one cached method.</exception>
    public int Loads<TService>(Func<TService, object?> call, object? serviceKey = null) where TService : class
    {
        ArgumentNullException.ThrowIfNull(call);
        return Loads(_provider.GetRequiredService<IMethodCacheKeys>().For(call, serviceKey));
    }

    /// <summary>
    /// How many times the factory of the entry with <paramref name="key"/> ran, such as one cached with
    /// <c>GetOrCreateAsync</c>, since the container was built or <see cref="Clear"/> was called. The batch factory of
    /// <c>GetOrCreateManyAsync</c> isn't counted per key.
    /// </summary>
    /// <param name="key">The entry's key, as passed to the cache manager or returned by <see cref="IMethodCacheKeys.For{TService}"/>.</param>
    public int Loads(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate) return _loads.GetValueOrDefault(key);
    }

    /// <summary>
    /// The tags invalidated so far by <c>[CacheInvalidate]</c> methods, <see cref="ICacheManager"/> calls and packages
    /// such as MethodCache.EntityFrameworkCore, in the order of the invalidations. A tag invalidated twice is listed twice.
    /// </summary>
    public IReadOnlyList<string> InvalidatedTags
    {
        get { lock (_gate) return _tags.ToArray(); }
    }

    /// <summary>The keys invalidated so far, in the order of the invalidations. A key invalidated twice is listed twice.</summary>
    public IReadOnlyList<string> InvalidatedKeys
    {
        get { lock (_gate) return _keys.ToArray(); }
    }

    /// <summary>Forgets everything recorded so far, such as the loads and invalidations of a test's setup.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _loads.Clear();
            _tags.Clear();
            _keys.Clear();
        }
    }

    private sealed class EventSink(CacheRecorder recorder) : ICacheEventSink
    {
        public void Loaded(string key)
        {
            lock (recorder._gate) recorder._loads[key] = recorder._loads.GetValueOrDefault(key) + 1;
        }

        public void Invalidated(bool tags, IReadOnlyList<string> targets)
        {
            lock (recorder._gate) (tags ? recorder._tags : recorder._keys).AddRange(targets);
        }
    }
}
