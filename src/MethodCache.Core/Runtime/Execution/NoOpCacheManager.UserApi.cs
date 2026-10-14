namespace MethodCache.Extensibility;

public partial class NoOpCacheManager
{
    /// <inheritdoc />
    public virtual ValueTask<T> GetOrCreateAsync<T>(string key, Func<CacheContext, CancellationToken, ValueTask<T>> factory,
        CacheEntryOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A cache key must be provided.", nameof(key));
        return new(RunAsync(options?.Runtime?.MethodId ?? CacheKeyLookup.MethodName,
            () => factory(new CacheContext(key), cancellationToken).AsTask()));
    }

    /// <inheritdoc />
    public virtual ValueTask<CacheLookupResult<T>> TryGetAsync<T>(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A cache key must be provided.", nameof(key));
        return new(CacheLookupResult<T>.Miss);
    }

    /// <inheritdoc />
    public virtual ValueTask<InvalidationResult> InvalidateKeysAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
        => Invalidate(keys, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<InvalidationResult> InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken)
        => Invalidate(tags, cancellationToken);

    private static ValueTask<InvalidationResult> Invalidate(IEnumerable<string> values, CancellationToken token)
    {
        var targets = InMemoryCacheManager.ValidateTargets(values);
        token.ThrowIfCancellationRequested();
        return new(new InvalidationResult { Requested = targets.Length, Local = targets.Length });
    }
}
