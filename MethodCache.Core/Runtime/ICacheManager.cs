using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.KeyGeneration;

namespace MethodCache.Core.Runtime
{
    /// <summary>
    /// Manages caching operations with support for L1 (in-memory) and L2 (distributed) cache layers.
    /// Provides methods for retrieving cached values, invalidating cache entries, and managing cache lifecycle.
    /// </summary>
    /// <remarks>
    /// ICacheManager is the core interface for all caching operations in MethodCache.
    /// It supports:
    /// - Multi-layer caching (L1/L2)
    /// - Tag-based invalidation for bulk cache clearing
    /// - Custom key generation strategies
    /// - Pattern-based cache key matching
    /// </remarks>
    /// <example>
    /// Basic usage with GetOrCreateAsync:
    /// <code>
    /// var keyGen = new FastHashKeyGenerator();
    /// var user = await cacheManager.GetOrCreateAsync(
    ///     methodName: "GetUser",
    ///     args: new object[] { userId },
    ///     factory: () => database.GetUserAsync(userId),
    ///     descriptor: descriptor,
    ///     keyGenerator: keyGen
    /// );
    /// </code>
    /// </example>
    public interface ICacheManager
    {
        // ============= New CacheRuntimePolicy-based methods (preferred) =============

        /// <summary>
        /// Retrieves a cached value or executes the factory function to create and cache a new value.
        /// This is the primary method for caching method results.
        /// </summary>
        /// <typeparam name="T">The type of value being cached</typeparam>
        /// <param name="methodName">Name of the method being cached (used for key generation)</param>
        /// <param name="args">Method arguments (used for key generation to differentiate cache entries)</param>
        /// <param name="factory">Factory function to execute if cache miss occurs</param>
        /// <param name="policy">Runtime policy containing cache configuration and options</param>
        /// <param name="keyGenerator">Strategy for generating cache keys from method name and arguments</param>
        /// <returns>The cached or newly created value</returns>
        Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator);

        /// <summary>
        /// Attempts to retrieve a value from the cache without executing a factory function.
        /// This is a fast, read-only path that bypasses factory execution and reduces overhead.
        /// </summary>
        /// <typeparam name="T">Type of cached value</typeparam>
        /// <param name="methodName">Method name for key generation</param>
        /// <param name="args">Method arguments for key generation</param>
        /// <param name="policy">Runtime policy for key generation</param>
        /// <param name="keyGenerator">Key generator instance</param>
        /// <returns>The cached value if found, or default(T) if not in cache</returns>
        ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator);

        /// <summary>
        /// Ultra-fast cache lookup that bypasses key generation, policy checks, and statistics.
        /// Use this when you have a pre-computed cache key and need minimal latency.
        /// This method only performs basic expiration checking.
        /// </summary>
        /// <typeparam name="T">Type of cached value</typeparam>
        /// <param name="cacheKey">Pre-computed cache key (must match the key used during cache population)</param>
        /// <returns>The cached value if found and not expired, or default(T) if not in cache</returns>
        /// <remarks>
        /// This method is optimized for maximum performance:
        /// - Skips key generation (uses pre-computed key)
        /// - Skips policy lookup and construction
        /// - Minimal expiration checking
        /// - No statistics tracking (if disabled)
        /// - No LRU updates (if disabled)
        /// Use this for hot paths where every microsecond counts.
        /// </remarks>
        ValueTask<T?> TryGetFastAsync<T>(string cacheKey);

        /// <summary>
        /// Retrieves a cached value or executes the factory function using a pre-computed cache key.
        /// This fast-path method is optimized for methods with simple parameters where the cache key
        /// can be computed inline without heavyweight serialization.
        /// </summary>
        /// <typeparam name="T">The type of value being cached</typeparam>
        /// <param name="cacheKey">Pre-computed cache key (must be deterministic and unique for the cached method+args)</param>
        /// <param name="methodName">Name of the method being cached (for metrics/diagnostics)</param>
        /// <param name="factory">Factory function to execute if cache miss occurs</param>
        /// <param name="policy">Runtime policy containing cache configuration and options</param>
        /// <returns>The cached or newly created value</returns>
        /// <remarks>
        /// This overload is designed for source-generated code that can compute cache keys inline
        /// for simple parameter types (string, int, Guid, etc.) without MessagePack serialization overhead.
        /// Use this for maximum performance when you can deterministically generate cache keys.
        /// </remarks>
        Task<T> GetOrCreateFastAsync<T>(string cacheKey, string methodName, Func<Task<T>> factory, CacheRuntimePolicy policy);

        /// <summary>
        /// Invalidates all cache entries associated with the specified tags.
        /// Use this for bulk invalidation when related data changes.
        /// </summary>
        /// <param name="tags">One or more tags identifying cache entries to invalidate</param>
        /// <returns>A task representing the asynchronous invalidation operation</returns>
        /// <example>
        /// <code>
        /// // Invalidate all product-related caches
        /// await cacheManager.InvalidateByTagsAsync("products", "catalog");
        ///
        /// // Invalidate caches for a specific user
        /// await cacheManager.InvalidateByTagsAsync($"user:{userId}");
        /// </code>
        /// </example>
        Task InvalidateByTagsAsync(params string[] tags);

        /// <summary>
        /// Invalidates cache entries with the specified exact keys.
        /// Use this for precise invalidation of specific cache entries.
        /// </summary>
        /// <param name="keys">One or more cache keys to invalidate</param>
        /// <returns>A task representing the asynchronous invalidation operation</returns>
        /// <example>
        /// <code>
        /// await cacheManager.InvalidateByKeysAsync(
        ///     "GetUser_123",
        ///     "GetUser_456"
        /// );
        /// </code>
        /// </example>
        Task InvalidateByKeysAsync(params string[] keys);

        /// <summary>
        /// Invalidates cache entries matching a wildcard pattern.
        /// Supported patterns depend on the cache provider (e.g., Redis supports glob patterns).
        /// </summary>
        /// <param name="pattern">Pattern to match cache keys (e.g., "GetUser*" or "user:*:profile")</param>
        /// <returns>A task representing the asynchronous invalidation operation</returns>
        /// <example>
        /// <code>
        /// // Invalidate all user-related caches
        /// await cacheManager.InvalidateByTagPatternAsync("GetUser*");
        ///
        /// // Invalidate all caches for a specific tenant
        /// await cacheManager.InvalidateByTagPatternAsync($"tenant:{tenantId}:*");
        /// </code>
        /// </example>
        Task InvalidateByTagPatternAsync(string pattern);
    }
}
