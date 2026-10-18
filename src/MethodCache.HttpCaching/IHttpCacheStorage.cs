namespace MethodCache.HttpCaching;

/// <summary>
/// Defines the contract for HTTP cache storage implementations.
/// </summary>
internal interface IHttpCacheStorage
{
    long BodyBudget(string key, HttpCacheEntry metadata) => long.MaxValue;

    IHttpCacheStorage ForClient(string client, HttpCacheOptions options) => this;

    async ValueTask<bool> TrySetAsync(string key, HttpCacheEntry entry, TimeSpan lifetime, CancellationToken token)
    {
        await SetAsync(key, entry, lifetime, token).ConfigureAwait(false);
        return true;
    }

    ValueTask InvalidateManyAsync(IReadOnlyList<string> keys, CancellationToken token)
        => new(Task.WhenAll(keys.Select(key => InvalidateAsync(key, CancellationToken.None).AsTask())).WaitAsync(token));

    /// <summary>
    /// Reads the current publication generation for a URL key. Requests publish only within this generation.
    /// </summary>
    /// <param name="key">The URL cache key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current opaque generation. Storage that cannot lose its generations may return an empty string before the first invalidation.</returns>
    /// <remarks>
    /// Generations must be shared by every instance that shares entries, and a generation value must never be reused.
    /// When the generation record is missing and may have been lost (for example evicted by a distributed backend),
    /// do not return a value used before, such as the empty string: store and return a new unique generation, so a
    /// lost record only causes cache misses. A failed read must return a unique non-reusable generation or throw, so
    /// it cannot make an older namespace visible.
    /// </remarks>
    ValueTask<string> GetGenerationAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes a URL's publication generation to a unique value, making previously published responses unreachable.
    /// </summary>
    /// <param name="key">The URL cache key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Only invalidations may change a generation. Each change must publish a fresh value, including after a retry;
    /// ordinary response writers must never replace it. Implementations may group keys to bound generation storage.
    /// The call has done its job once the generation differs from the one current when it started: a fresh value
    /// stored by a concurrent invalidation of the same key or group is as good as its own.
    /// </remarks>
    ValueTask InvalidateAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a cache entry by its key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cache entry if found, otherwise null.</returns>
    ValueTask<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a cache entry with the specified key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="entry">The cache entry to store.</param>
    /// <param name="timeToLive">How long to keep the entry. This covers its freshness plus the time a stale entry is kept for revalidation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask SetAsync(string key, HttpCacheEntry entry, TimeSpan timeToLive, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a cache entry by its key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all cache entries.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}
