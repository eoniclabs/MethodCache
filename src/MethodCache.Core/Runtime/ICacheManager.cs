namespace MethodCache;

/// <summary>Provides explicit-key caching and invalidation operations.</summary>
public interface ICacheManager
{
    /// <summary>Gets a cached result or executes the factory with the invocation context and caller token.</summary>
    ValueTask<T> GetOrCreateAsync<T>(string key, Func<CacheContext, CancellationToken, ValueTask<T>> factory,
        CacheEntryOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Reads a value and reports presence separately, including cached null, zero and false.</summary>
    ValueTask<CacheLookupResult<T>> TryGetAsync<T>(string key, CancellationToken cancellationToken = default);

    /// <summary>Invalidates distinct logical keys, returning separate local, storage and publication outcomes.</summary>
    ValueTask<InvalidationResult> InvalidateKeysAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default);

    /// <summary>Invalidates distinct application tags, returning separate local, storage and publication outcomes.</summary>
    ValueTask<InvalidationResult> InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default);
}
