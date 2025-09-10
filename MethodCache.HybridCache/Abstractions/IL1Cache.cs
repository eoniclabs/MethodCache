using System;
using System.Threading.Tasks;

namespace MethodCache.HybridCache.Abstractions
{
    /// <summary>
    /// Defines a Level 1 (in-memory) cache for the hybrid cache pattern.
    /// </summary>
    public interface IL1Cache : IDisposable
    {
        /// <summary>
        /// Gets a value from the L1 cache.
        /// </summary>
        /// <typeparam name="T">Type of the cached value</typeparam>
        /// <param name="key">Cache key</param>
        /// <returns>The cached value or null if not found</returns>
        Task<T?> GetAsync<T>(string key);

        /// <summary>
        /// Sets a value in the L1 cache.
        /// </summary>
        /// <typeparam name="T">Type of the value to cache</typeparam>
        /// <param name="key">Cache key</param>
        /// <param name="value">Value to cache</param>
        /// <param name="expiration">Expiration time</param>
        Task SetAsync<T>(string key, T value, TimeSpan expiration);

        /// <summary>
        /// Removes a value from the L1 cache.
        /// </summary>
        /// <param name="key">Cache key</param>
        /// <returns>True if the item was removed, false otherwise</returns>
        Task<bool> RemoveAsync(string key);

        /// <summary>
        /// Clears all items from the L1 cache.
        /// </summary>
        Task ClearAsync();

        /// <summary>
        /// Gets statistics about the L1 cache.
        /// </summary>
        Task<L1CacheStats> GetStatsAsync();

        /// <summary>
        /// Removes multiple keys from the L1 cache.
        /// </summary>
        /// <param name="keys">Keys to remove</param>
        /// <returns>Number of items removed</returns>
        Task<int> RemoveMultipleAsync(params string[] keys);

        /// <summary>
        /// Checks if a key exists in the L1 cache.
        /// </summary>
        /// <param name="key">Cache key</param>
        /// <returns>True if the key exists, false otherwise</returns>
        Task<bool> ExistsAsync(string key);
    }

    /// <summary>
    /// Statistics for the L1 cache.
    /// </summary>
    public class L1CacheStats
    {
        /// <summary>
        /// Total number of cache hits.
        /// </summary>
        public long Hits { get; init; }

        /// <summary>
        /// Total number of cache misses.
        /// </summary>
        public long Misses { get; init; }

        /// <summary>
        /// Total number of evictions.
        /// </summary>
        public long Evictions { get; init; }

        /// <summary>
        /// Current number of entries in the cache.
        /// </summary>
        public long Entries { get; init; }

        /// <summary>
        /// Approximate memory usage in bytes.
        /// </summary>
        public long MemoryUsage { get; init; }

        /// <summary>
        /// Hit ratio (hits / (hits + misses)).
        /// </summary>
        public double HitRatio => Hits + Misses > 0 ? (double)Hits / (Hits + Misses) : 0;
    }
}