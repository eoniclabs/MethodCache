using MethodCache.Core.Runtime;
using MethodCache.ETags.Models;

namespace MethodCache.ETags.Abstractions
{
    /// <summary>
    /// Provides ETag-aware caching functionality with hybrid L1/L2 cache support.
    /// Enables conditional HTTP caching and cross-instance ETag consistency.
    /// </summary>
    public interface IETagCacheManager
    {
        /// <summary>
        /// Gets or creates a cached value with ETag support, leveraging hybrid L1/L2 caching.
        /// </summary>
        /// <typeparam name="T">The type of the cached value</typeparam>
        /// <param name="key">Cache key</param>
        /// <param name="factory">Factory function to create the value and ETag</param>
        /// <param name="ifNoneMatch">Client's If-None-Match header value</param>
        /// <param name="settings">Cache settings</param>
        /// <param name="forceRefresh">Whether to bypass cache and always execute factory</param>
        /// <returns>ETag cache result indicating hit, miss, or not modified</returns>
        Task<ETagCacheResult<T>> GetOrCreateWithETagAsync<T>(
            string key,
            Func<Task<ETagCacheEntry<T>>> factory,
            string? ifNoneMatch = null,
            CacheRuntimeDescriptor? descriptor = null,
            bool forceRefresh = false);

        /// <summary>
        /// Gets or creates a cached value with context-aware ETag generation.
        /// The factory receives the current ETag to enable conditional processing.
        /// </summary>
        /// <typeparam name="T">The type of the cached value</typeparam>
        /// <param name="key">Cache key</param>
        /// <param name="factory">Factory function that receives current ETag and returns new entry</param>
        /// <param name="ifNoneMatch">Client's If-None-Match header value</param>
        /// <param name="descriptor">Runtime descriptor containing cache policy</param>
        /// <param name="forceRefresh">Whether to bypass cache and always execute factory</param>
        /// <returns>ETag cache result</returns>
        Task<ETagCacheResult<T>> GetOrCreateWithETagAsync<T>(
            string key,
            Func<string?, Task<ETagCacheEntry<T>>> factory,
            string? ifNoneMatch = null,
            CacheRuntimeDescriptor? descriptor = null,
            bool forceRefresh = false);

        /// <summary>
        /// Invalidates a cached entry and its ETag from all cache layers.
        /// </summary>
        /// <param name="key">Cache key to invalidate</param>
        Task InvalidateETagAsync(string key);

        /// <summary>
        /// Invalidates multiple cached entries by their keys.
        /// </summary>
        /// <param name="keys">Cache keys to invalidate</param>
        Task InvalidateETagsAsync(params string[] keys);

        /// <summary>
        /// Gets the current ETag for a cached entry without retrieving the value.
        /// </summary>
        /// <param name="key">Cache key</param>
        /// <returns>Current ETag or null if not found</returns>
        Task<string?> GetETagAsync(string key);

        /// <summary>
        /// Checks if the provided ETag matches the current cached ETag.
        /// </summary>
        /// <param name="key">Cache key</param>
        /// <param name="etag">ETag to compare</param>
        /// <returns>True if ETags match, false otherwise</returns>
        Task<bool> IsETagValidAsync(string key, string etag);
    }
}