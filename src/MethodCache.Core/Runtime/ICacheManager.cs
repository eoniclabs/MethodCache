namespace MethodCache;

/// <summary>Provides explicit-key caching and invalidation operations.</summary>
public interface ICacheManager
{
    /// <summary>
    /// Gets a cached result or runs <paramref name="factory"/> with <paramref name="state"/> and the caller token.
    /// Passing what the factory needs as state lets it be a <c>static</c> lambda, so a call allocates no closure.
    /// </summary>
    ValueTask<T> GetOrCreateAsync<TState, T>(string key, TState state, Func<TState, CancellationToken, ValueTask<T>> factory,
        CacheEntryOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Reads a logical key in the optional version, reporting presence separately, including cached null, zero and false.</summary>
    ValueTask<CacheLookupResult<T>> TryGetAsync<T>(string key, int? version = null, CancellationToken cancellationToken = default);

    /// <summary>Invalidates distinct logical keys in the optional version, returning separate local, storage and publication outcomes.</summary>
    ValueTask<InvalidationResult> InvalidateKeysAsync(IEnumerable<string> keys, int? version = null, CancellationToken cancellationToken = default);

    /// <summary>Invalidates distinct application tags, returning separate local, storage and publication outcomes.</summary>
    ValueTask<InvalidationResult> InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default);
}
