namespace MethodCache.HttpCaching.Storage;

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
    Task<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a cache entry with the specified key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="entry">The cache entry to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetAsync(string key, HttpCacheEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a cache entry by its key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all cache entries.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ClearAsync(CancellationToken cancellationToken = default);
}