namespace MethodCache;

/// <summary>Provides explicit-key caching and invalidation operations.</summary>
/// <remarks>
/// An implementation of your own stores an entry under <see cref="MethodCacheKeys.StoredKey"/> of the key and version
/// it gets: <c>StoredKey(key, options?.Version)</c> in <see cref="GetOrCreateAsync{TState, T}"/> and
/// <c>StoredKey(key, version)</c> in the other methods, so a key and version name the same entry in every method.
/// </remarks>
public interface ICacheManager
{
    /// <summary>
    /// Gets a cached result or runs <paramref name="factory"/> with <paramref name="state"/> and the caller token.
    /// Passing what the factory needs as state lets it be a <c>static</c> lambda, so a call allocates no closure.
    /// </summary>
    /// <param name="key">The entry's key.</param>
    /// <param name="state">Passed to <paramref name="factory"/>.</param>
    /// <param name="factory">Produces the value on a miss.</param>
    /// <param name="options">
    /// How the entry is cached, or <c>null</c> for the defaults. A call from a cached method passes a <paramref name="key"/>
    /// that already carries the method's version and options whose <see cref="CacheEntryOptions.Version"/> is <c>null</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels this caller's wait.</param>
    ValueTask<T> GetOrCreateAsync<TState, T>(string key, TState state, Func<TState, CancellationToken, ValueTask<T>> factory,
        CacheEntryOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Reads a logical key in the optional version, reporting presence separately, including cached null, zero and false.</summary>
    /// <param name="key">The entry's key.</param>
    /// <param name="version">
    /// The version the entry was cached with (<see cref="CacheEntryOptions.Version"/>), or <c>null</c> for an unversioned
    /// entry. A key from <see cref="IMethodCacheKeys"/> already carries its version: pass <c>null</c> with it.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<CacheLookupResult<T>> TryGetAsync<T>(string key, int? version = null, CancellationToken cancellationToken = default);

    /// <summary>Invalidates distinct logical keys in the optional version, returning separate local, storage and publication outcomes.</summary>
    /// <param name="keys">The entries' keys.</param>
    /// <param name="version">
    /// The version the entries were cached with (<see cref="CacheEntryOptions.Version"/>), or <c>null</c> for unversioned
    /// entries. A key from <see cref="IMethodCacheKeys"/> already carries its version: pass <c>null</c> with it.
    /// </param>
    /// <param name="cancellationToken">Cancels this caller's wait; the invalidation itself is not undone.</param>
    ValueTask<InvalidationResult> InvalidateKeysAsync(IEnumerable<string> keys, int? version = null, CancellationToken cancellationToken = default);

    /// <summary>Invalidates distinct application tags, returning separate local, storage and publication outcomes.</summary>
    ValueTask<InvalidationResult> InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default);
}
