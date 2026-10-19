using MethodCache.Extensibility;

namespace MethodCache;

public sealed partial class NoOpCacheManager
{
    /// <inheritdoc />
    public ValueTask<T> GetOrCreateAsync<TState, T>(string key, TState state, Func<TState, CancellationToken, ValueTask<T>> factory,
        CacheEntryOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A cache key must be provided.", nameof(key));
        return new(RunAsync(options?.Runtime?.MethodId ?? CacheKeyLookup.MethodName,
            () => factory(state, cancellationToken).AsTask()));
    }

    /// <inheritdoc />
    public ValueTask<CacheLookupResult<T>> TryGetAsync<T>(string key, int? version = null, CancellationToken cancellationToken = default)
    {
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A cache key must be provided.", nameof(key));
        return new(CacheLookupResult<T>.Miss);
    }

    /// <inheritdoc />
    public ValueTask<InvalidationResult> InvalidateKeysAsync(IEnumerable<string> keys, int? version = null, CancellationToken cancellationToken = default)
    {
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        return Invalidate(keys, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<InvalidationResult> InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
        => Invalidate(tags, cancellationToken);

    private static ValueTask<InvalidationResult> Invalidate(IEnumerable<string> values, CancellationToken token)
    {
        var targets = InMemoryCacheManager.ValidateTargets(values);
        token.ThrowIfCancellationRequested();
        return new(new InvalidationResult { Requested = targets.Length, Local = targets.Length });
    }
}
