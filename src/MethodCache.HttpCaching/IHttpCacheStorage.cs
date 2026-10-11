namespace MethodCache.HttpCaching;

/// <summary>
/// Defines the contract for HTTP cache storage implementations.
/// </summary>
public interface IHttpCacheStorage
{
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
