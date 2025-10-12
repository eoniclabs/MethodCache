using MethodCache.Core.Infrastructure;

namespace MethodCache.Core.Storage.Abstractions
{
    /// <summary>
    /// Defines a memory cache interface for in-process caching operations.
    /// </summary>
    public interface IMemoryCache : IDisposable
    {
        /// <summary>
        /// Gets a value from the memory cache.
        /// </summary>
        /// <typeparam name="T">Type of the cached value</typeparam>
        /// <param name="key">Cache key</param>
        /// <returns>The cached value or null if not found</returns>
        ValueTask<T?> GetAsync<T>(string key);

        /// <summary>
        /// Sets a value in the memory cache.
        /// </summary>
        /// <typeparam name="T">Type of the value to cache</typeparam>
        /// <param name="key">Cache key</param>
        /// <param name="value">Value to cache</param>
        /// <param name="expiration">Expiration time</param>
        Task SetAsync<T>(string key, T value, TimeSpan expiration);

        /// <summary>
        /// Removes a value from the memory cache.
        /// </summary>
        /// <param name="key">Cache key</param>
        /// <returns>True if the item was removed, false otherwise</returns>
        ValueTask<bool> RemoveAsync(string key);

        /// <summary>
        /// Clears all items from the memory cache.
        /// </summary>
        ValueTask ClearAsync();

        /// <summary>
        /// Gets statistics about the memory cache.
        /// </summary>
        ValueTask<ICacheStats> GetStatsAsync();

        /// <summary>
        /// Removes multiple keys from the memory cache.
        /// </summary>
        /// <param name="keys">Keys to remove</param>
        /// <returns>Number of items removed</returns>
        ValueTask<int> RemoveMultipleAsync(params string[] keys);

        /// <summary>
        /// Checks if a key exists in the memory cache.
        /// </summary>
        /// <param name="key">Cache key</param>
        /// <returns>True if the key exists, false otherwise</returns>
        ValueTask<bool> ExistsAsync(string key);
    }
}